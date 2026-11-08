using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;

namespace Modulix.Core.Approvals;

/// <summary>
/// Core'un kural yeteneği (ADR-0027, Dilim 4c): sektörden bağımsız tek aksiyon, "Onaya gönder". Core kayıt türü beyan etmez; aksiyon
/// hedef kaydı kuralın kayıt türünden alır ve talep sahibini o türün <see cref="AutomationEntityDefinition.OwnerField"/> alanından bulur.
/// </summary>
public static class ApprovalAutomation
{
    public const string RequestAction = "core.approval.request";

    public static AutomationCapabilities Capabilities { get; } = new()
    {
        Actions =
        [
            AutomationActionRegistration.For<RequestApprovalAction>(new AutomationActionDefinition(
                RequestAction,
                "Onaya gönder",
                "Kayda bağlı bir onay talebi açar. Talep sahibi kaydın sorumlusudur; kendi talebine karar veremez. Aynı kural adımı talebi bir kez açar; "
                + "aynı kayıt ve süreç için bekleyen talep varsa ikincisi açılmaz.",
                [
                    new AutomationParameter("process", "Onay süreci", AutomationFieldType.Text)
                    {
                        MaxLength = ApprovalDefinition.NameMaxLength,
                        AllowFieldBinding = false,
                        Description = "Yönetim > Onay süreçlerinde tanımlı, etkin sürecin adı.",
                    },
                    new AutomationParameter("title", "Başlık", AutomationFieldType.Text)
                    {
                        MaxLength = ApprovalRequest.TitleMaxLength,
                        Description = "Onaycının göreceği başlık. Kayıt değişkeni için {{alan}} yazılabilir (ör. {{crm.opportunity.title}} indirimi).",
                    },
                    new AutomationParameter("description", "Açıklama", AutomationFieldType.Text, Required: false)
                    {
                        MaxLength = ApprovalRequest.DescriptionMaxLength,
                    },
                    new AutomationParameter("amount", "Tutar", AutomationFieldType.Number, Required: false)
                    {
                        Description = "Sürecin tutar eşikleri için. Kayıttaki bir tutar ya da sayı alanı seçilebilir.",
                    },
                    new AutomationParameter("currency", "Para birimi", AutomationFieldType.Text, Required: false)
                    {
                        MaxLength = 3,
                        AllowFieldBinding = false,
                        Description = "Tutar verildiyse para birimi (ör. TRY); sürecin para birimiyle aynı olmalı.",
                    },
                ],
                [CoreModule.Permissions.RequestApprovals],
                [],
                AutomationSideEffect.Write,
                SupportsDryRun: true)),
        ],
    };
}

/// <summary>
/// Yayında onay sürecinin adını denetler (sabit metin): firmada o adla <b>etkin</b> bir süreç olmalı. Süreç sonradan kapanır ya da
/// silinirse kural çalışırken adım açıkça başarısız olur (<see cref="RequestApprovalAction"/>).
/// </summary>
internal sealed class ApprovalProcessCheck(IApprovalService approvals) : IAutomationStepCheck
{
    public string ActionKey => ApprovalAutomation.RequestAction;

    public async Task<IReadOnlyList<AutomationIssue>> CheckAsync(AutomationActionStep step, string path, CancellationToken cancellationToken)
    {
        // Eksik ya da bağlama olan parametreyi şema denetimi zaten bildirir; burada yalnızca sabit ad denetlenir.
        if (step.Parameters?["process"] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var definitions = await approvals.ListDefinitionsAsync(activeOnly: true, cancellationToken);
        return definitions.Any(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? []
            : [new AutomationIssue($"{path}.parameters.process", $"'Onay süreci': \"{name.Trim()}\" adında etkin bir onay süreci yok.")];
    }
}

/// <summary>
/// "Onaya gönder" aksiyonu. Talep <see cref="IApprovalService.OpenForRecordAsync"/> ile açılır (denetim, e-posta ve bildirim kutusu dahil
/// tek işlemde); işleyici <c>DbContext</c>'e yazmaz. Talep sahibi kaydın sorumlusudur: dört göz onun için geçerlidir. Sorumlu yoksa ya da
/// süreçte sorumludan başka onaycı kalmıyorsa adım açıkça başarısız olur (sahipsiz talep üretilmez). Dry-run hiçbir şey yazmaz.
/// Özet kişisel veri taşımaz (başlık ve kişi adı yazılmaz).
/// </summary>
internal sealed class RequestApprovalAction(IApprovalService approvals, IRecordPipeline pipeline, ModuleCatalog modules) : IAutomationAction
{
    public async Task<AutomationActionResult> ExecuteAsync(
        AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        if (request.Target is not { } target)
        {
            return Failed("Hedef kayıt yok.");
        }

        var process = Text(request.Parameters["process"])?.Trim();
        if (string.IsNullOrEmpty(process))
        {
            return Failed("Onay süreci adı boş.");
        }

        var title = Text(request.Parameters["title"])?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return Failed("Başlık boş.");
        }

        if (!TryAmount(request.Parameters["amount"], out var amount))
        {
            return Failed("Tutar sayı olmalı.");
        }

        var definition = (await approvals.ListDefinitionsAsync(activeOnly: true, cancellationToken))
            .FirstOrDefault(d => string.Equals(d.Name, process, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return Failed($"Etkin onay süreci bulunamadı: {process}.");
        }

        if (await OwnerOfAsync(target, cancellationToken) is not { } owner)
        {
            return Failed("Talep sahibi yok: kaydın sorumlusu boş ya da bu kayıt türünde sorumlu alanı tanımlı değil.");
        }

        if (request.DryRun)
        {
            return new AutomationActionResult(AutomationActionStatus.Planned, $"Onay talebi açılacak (süreç: {definition.Name}).");
        }

        try
        {
            var opened = await approvals.OpenForRecordAsync(
                new ApprovalRecordRequest(
                    new ApprovalRequestInput(definition.Id, Cut(title, ApprovalRequest.TitleMaxLength), Text(request.Parameters["description"]), amount, Text(request.Parameters["currency"])),
                    owner,
                    target.EntityKey,
                    target.Id,
                    request.IdempotencyKey),
                cancellationToken);

            return new AutomationActionResult(
                opened.AlreadyExisted ? AutomationActionStatus.Skipped : AutomationActionStatus.Succeeded,
                opened.AlreadyExisted ? "Bu kayıt için onay talebi zaten açılmıştı." : "Onay talebi açıldı.")
            {
                Effect = new RecordRef(CoreModule.AuditedEntityKeys.ApprovalRequest, opened.Request.Id),
            };
        }
        catch (ApprovalException ex)
        {
            // İş kuralı ihlali (ör. dört göz sağlanamıyor, tutar para birimi uyuşmuyor): tekrar denemek sonucu değiştirmez.
            return Failed(ex.Message);
        }
    }

    private async Task<Guid?> OwnerOfAsync(RecordRef target, CancellationToken cancellationToken)
    {
        var field = modules.FindAutomationEntity(target.EntityKey)?.Entity.OwnerField;
        if (field is null)
        {
            return null;
        }

        var record = await pipeline.GetAsync(target, cancellationToken);
        return record.IsSuccess && Guid.TryParse(Text(record.Value!.Values[field]), out var owner) && owner != Guid.Empty ? owner : null;
    }

    private static AutomationActionResult Failed(string summary) => new(AutomationActionStatus.Failed, summary);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static bool TryAmount(JsonNode? node, out decimal? amount)
    {
        amount = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<decimal>(out var number))
        {
            amount = number;
            return true;
        }

        var text = Text(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            amount = number;
            return true;
        }

        return false;
    }
}
