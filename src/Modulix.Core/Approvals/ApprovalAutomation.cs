using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;

namespace Modulix.Core.Approvals;

/// <summary>
/// Core'un kural yeteneği (ADR-0027, Dilim 4c-2): sektörden bağımsız tek aksiyon, "Onaya gönder". Core kayıt türü beyan etmez; aksiyon
/// hedef kaydı kuralın kayıt türünden alır, talep sahibini o türün <see cref="AutomationEntityDefinition.OwnerField"/> alanından bulur.
/// Süreç kayda bağlı bir onay sürecidir (Dilim 4c): tutar ve para birimi parametre değildir, kayıttan okunur.
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
                "Kaydı, kayıt türüne bağlı bir onay sürecine gönderir; tutar ve para birimi kayıttan okunur. Talep sahibi kaydın sorumlusudur; "
                + "kendi talebine karar veremez. Aynı kural adımı talebi bir kez açar; kayıt için bekleyen talep varsa ikincisi açılmaz.",
                [
                    new AutomationParameter("process", "Onay süreci", AutomationFieldType.Text)
                    {
                        MaxLength = ApprovalDefinition.NameMaxLength,
                        AllowFieldBinding = false,
                        Description = "Yönetim > Onay süreçlerinde tanımlı, bu kayıt türüne bağlı etkin sürecin adı.",
                    },
                    new AutomationParameter("title", "Başlık", AutomationFieldType.Text, Required: false)
                    {
                        MaxLength = ApprovalRequest.TitleMaxLength,
                        Description = "Onaycının göreceği başlık; boşsa kaydın adı yazılır. Kayıt değişkeni için {{alan}} yazılabilir (ör. {{crm.opportunity.title}} indirimi).",
                    },
                    new AutomationParameter("description", "Açıklama", AutomationFieldType.Text, Required: false)
                    {
                        MaxLength = ApprovalRequest.DescriptionMaxLength,
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
/// Yayında onay sürecinin adını denetler (sabit metin): firmada o adla <b>etkin</b> ve kuralın kayıt türüne <b>bağlı</b> bir süreç olmalı.
/// Süreç sonradan kapanır, silinir ya da başka türe bağlanırsa kural çalışırken adım açıkça başarısız olur (<see cref="RequestApprovalAction"/>).
/// </summary>
internal sealed class ApprovalProcessCheck(IApprovalService approvals) : IAutomationStepCheck
{
    public string ActionKey => ApprovalAutomation.RequestAction;

    public async Task<IReadOnlyList<AutomationIssue>> CheckAsync(string entityKey, AutomationActionStep step, string path, CancellationToken cancellationToken)
    {
        // Eksik ya da bağlama olan parametreyi şema denetimi zaten bildirir; burada yalnızca sabit ad denetlenir.
        if (step.Parameters?["process"] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var definitions = await approvals.ListDefinitionsAsync(activeOnly: true, cancellationToken);
        var definition = definitions.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return [new AutomationIssue($"{path}.parameters.process", $"'Onay süreci': \"{name.Trim()}\" adında etkin bir onay süreci yok.")];
        }

        return definition.TargetEntityKey == entityKey
            ? []
            : [new AutomationIssue($"{path}.parameters.process", $"'Onay süreci': \"{definition.Name}\" süreci bu kayıt türüne bağlı değil; Yönetim > Onay süreçlerinde kayıt türünü seçin.")];
    }
}

/// <summary>
/// "Onaya gönder" aksiyonu. Talep <see cref="IApprovalService.OpenForRecordAsync"/> ile açılır (denetim, e-posta ve bildirim kutusu dahil
/// tek işlemde); işleyici <c>DbContext</c>'e yazmaz. Kayıt, tutar ve para birimi kayda bağlı onayın kurallarıyla (Dilim 4c) kayıttan okunur.
/// Talep sahibi kaydın sorumlusudur: dört göz onun için geçerlidir. Sorumlu yoksa ya da süreçte sorumludan başka onaycı kalmıyorsa adım
/// açıkça başarısız olur (sahipsiz talep üretilmez). Dry-run hiçbir şey yazmaz. Özet kişisel veri taşımaz (başlık ve kişi adı yazılmaz).
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

        var definition = (await approvals.ListDefinitionsAsync(activeOnly: true, cancellationToken))
            .FirstOrDefault(d => string.Equals(d.Name, process, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return Failed($"Etkin onay süreci bulunamadı: {process}.");
        }

        if (definition.TargetEntityKey != target.EntityKey)
        {
            return Failed($"\"{definition.Name}\" süreci bu kayıt türüne bağlı değil.");
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
            // Başlık boşsa kayda bağlı onay kaydın adını yazar; tutar ve para birimi kayıttan okunur (girilen değer yok).
            var title = Text(request.Parameters["title"])?.Trim() ?? string.Empty;
            var opened = await approvals.OpenForRecordAsync(
                new ApprovalRecordRequest(
                    new ApprovalRequestInput(definition.Id, Cut(title, ApprovalRequest.TitleMaxLength), Text(request.Parameters["description"]), null, null, target.Id),
                    owner,
                    request.IdempotencyKey),
                cancellationToken);

            return new AutomationActionResult(
                opened.AlreadyExisted ? AutomationActionStatus.Skipped : AutomationActionStatus.Succeeded,
                opened.AlreadyExisted ? "Bu kayıt için onay talebi zaten açık." : "Onay talebi açıldı.")
            {
                Effect = new RecordRef(CoreModule.AuditedEntityKeys.ApprovalRequest, opened.Request.Id),
            };
        }
        catch (ApprovalException ex)
        {
            // İş kuralı ihlali (ör. dört göz sağlanamıyor, kayıt okunamıyor, tutar boş): tekrar denemek sonucu değiştirmez.
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
}
