using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Automation;

/// <summary>
/// "Takip aktivitesi oluştur" aksiyonu (<see cref="CrmAutomation.FollowUpAction"/>, ADR-0027 Dilim 3). Aktiviteyi <see cref="CrmFollowUpActivities"/> üzerinden,
/// yani kayıt boru hattıyla açar (izin, kapsam, audit, olay); işleyici <c>DbContext</c>'e yazmaz. Idempotency anahtarı aktiviteye <c>externalId</c> olarak yazılır:
/// aynı adımın ikinci denemesi ya da teslimi ikinci aktivite açmaz, mevcut olan <see cref="AutomationActionStatus.Skipped"/> ile bildirilir.
/// Atanan verilmezse hedef kaydın sorumlusuna atanır; sorumlu da yoksa aktivite açılmaz ve neden yazılır (sahipsiz aktivite üretilmez).
/// Dry-run hiçbir şey yazmaz, planı döner. Özet kişisel veri taşımaz (konu metni yazılmaz).
/// </summary>
internal sealed class CreateFollowUpAction(CrmFollowUpActivities followUps, IRecordPipeline pipeline, ITenantClock clock) : IAutomationAction
{
    private const int DefaultDueInDays = 1;
    private const int MaxDueInDays = 365;

    public async Task<AutomationActionResult> ExecuteAsync(
        AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        if (request.Target is not { } target)
        {
            return Failed("Hedef kayıt yok.");
        }

        var subject = Text(request.Parameters["subject"]);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Failed("Konu boş.");
        }

        if (!TryDays(request.Parameters["dueInDays"], out var days) || days is < 0 or > MaxDueInDays)
        {
            return Failed($"Gün sayısı 0-{MaxDueInDays} arasında olmalı.");
        }

        var assignee = Guid.TryParse(Text(request.Parameters["assigneeId"]), out var given) && given != Guid.Empty ? given : (Guid?)null;
        assignee ??= await OwnerOfAsync(target, cancellationToken);
        if (assignee is null)
        {
            return Failed("Atanacak kullanıcı yok: kaydın sorumlusu boş ve parametre verilmemiş.");
        }

        var due = (await clock.GetAsync(cancellationToken)).Today.AddDays(days);
        if (request.DryRun)
        {
            return new AutomationActionResult(AutomationActionStatus.Planned, $"Takip aktivitesi açılacak (son tarih {due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).");
        }

        var result = await followUps.CreateAsync(
            new Core.Contracts.Activities.FollowUpRequest(
                subject.Trim(), Text(request.Parameters["description"]), due, assignee, target.EntityKey, target.Id, request.IdempotencyKey),
            cancellationToken);
        if (result.ActivityId is not { } activityId)
        {
            return Failed(result.Unavailable ? "Aktivite modülü bu firma için kapalı." : result.Error ?? "Aktivite açılamadı.");
        }

        return new AutomationActionResult(
            result.AlreadyExisted ? AutomationActionStatus.Skipped : AutomationActionStatus.Succeeded,
            result.AlreadyExisted ? "Takip aktivitesi zaten açılmıştı." : "Takip aktivitesi açıldı.")
        {
            Effect = new RecordRef(CrmModule.Entities.Activity, activityId),
        };
    }

    private async Task<Guid?> OwnerOfAsync(RecordRef target, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(target, cancellationToken);
        return record.IsSuccess && Guid.TryParse(Text(record.Value!.Values["ownerId"]), out var owner) && owner != Guid.Empty ? owner : null;
    }

    private static AutomationActionResult Failed(string summary) => new(AutomationActionStatus.Failed, summary);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();

    private static bool TryDays(JsonNode? node, out int days)
    {
        days = DefaultDueInDays;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out days))
        {
            return true;
        }

        return int.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out days);
    }
}
