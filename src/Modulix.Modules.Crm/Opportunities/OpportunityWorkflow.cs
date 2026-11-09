using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Approvals;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Opportunities;

/// <summary>Fırsat eyleminin sonucu: güncel kayıt ile kapanışta açık kalan aktiviteler ve oluşturulan takip.</summary>
/// <param name="OpenActivities">Kapanıştan sonra bu fırsata bağlı açık aktivite sayısı. Aktiviteler SİLİNMEZ ve devredilmez; açık kalır.</param>
public sealed record OpportunityActionResult(
    RecordData Opportunity, int OpenActivities = 0, Guid? FollowUpActivityId = null, string? FollowUpWarning = null);

public sealed record WinOpportunityRequest(
    DateOnly? ActualCloseDate, decimal? WonAmount, string? WonCurrency, string? Reason, string? HandoverNote, string? Stage);

public sealed record LoseOpportunityRequest(
    string Reason, string? Note, Guid? WinningCompetitorId, DateOnly? RecontactDate, bool CreateFollowUp);

public sealed record ReopenOpportunityRequest(string Reason, string Stage, DateOnly ExpectedCloseDate, int? Probability);

/// <summary>Tarihçe satırı. <see cref="Details"/> kapanış tutarlarını içerir; tutar izni olmayan çağırana verilmez.</summary>
public sealed record OpportunityHistoryItem(
    Guid Id, string Kind, DateTimeOffset At, Guid? ChangedBy, int Cycle, string? FromStage, string? ToStage, string? FromStatus,
    string? ToStatus, DateOnly? FromCloseDate, DateOnly? ToCloseDate, string? Reason, string? Details);

/// <summary>
/// Fırsat yaşam döngüsü eylemleri (ADR-0017). Tek yazma yolu: eylemin izinlerine bağlı iş akışı kapısı açılır ve kayıt boru hattı
/// üzerinden güncelleme yapılır; kapsam, hassas alan, audit, olay, aşama–durum tutarlılığı ve tarihçe depoda uygulanır. Genel
/// kayıt uçları durumu, kapanış alanlarını ya da aşamayı (oluşturma sonrası) yazamaz. API, Kanban ve ileride yapay zekâ araçları
/// aynı servisi kullanır. Her eylem kaydın sürümünü ister (eşzamanlılık). Kazanma ve kaybetme açık iş komutudur ve iş olayını
/// (<c>crm.opportunity.won</c> / <c>crm.opportunity.lost</c>, ADR-0039) kayıt değişikliğiyle aynı işlemde yayınlar.
/// </summary>
public interface IOpportunityWorkflow
{
    Task<RecordResult<OpportunityActionResult>> ChangeStageAsync(Guid id, long version, string stage, string? note, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> WinAsync(Guid id, long version, WinOpportunityRequest request, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> LoseAsync(Guid id, long version, LoseOpportunityRequest request, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> CancelAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> HoldAsync(Guid id, long version, string reason, DateOnly reviewDate, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> ResumeAsync(Guid id, long version, string? note, CancellationToken cancellationToken);

    Task<RecordResult<OpportunityActionResult>> ReopenAsync(Guid id, long version, ReopenOpportunityRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Gerçekleşen bir müşteri temasını işler (olay aboneliği çağırır): son etkileşim yalnızca daha yeni bir temasla ilerler; aynı
    /// temasın tekrarı değişiklik, audit ya da olay üretmez (idempotent).
    /// </summary>
    Task<RecordResult<OpportunityActionResult>> RecordInteractionAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Fırsatın tarihçesi (eskiden yeniye). Fırsat okunabiliyorsa tarihçesi de okunur; tutar bilgisi tutar iznine bağlıdır.</summary>
    Task<RecordResult<IReadOnlyList<OpportunityHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Olay nedenleri (<see cref="RecordCommand.Reason"/>): genel güncelleme olayının yanında iş anlamını taşır.</summary>
public static class OpportunityEventReasons
{
    public const string StageChanged = "opportunity.stage-changed";
    public const string Won = "opportunity.won";
    public const string Lost = "opportunity.lost";
    public const string Cancelled = "opportunity.cancelled";
    public const string Held = "opportunity.held";
    public const string Resumed = "opportunity.resumed";
    public const string Reopened = "opportunity.reopened";
    public const string Interaction = "opportunity.interaction";
}

internal sealed class OpportunityWorkflow(
    IRecordPipeline pipeline,
    OpportunityWorkflowGate gate,
    IChoiceLists choiceLists,
    ICurrentAccess access,
    OpportunityHistoryReader historyReader,
    ISalesReportingPolicyProvider reportingPolicy,
    IBusinessEventPublisher events,
    IApprovalGate approvalGate,
    TimeProvider time) : IOpportunityWorkflow
{
    private const string Key = CrmModule.Entities.Opportunity;

    // Aşama değiştirme ve kapatma, yazma izni olan mevcut rollerin davranışı bozulmasın diye yazma iznini de kabul eder (ADR-0017);
    // yeniden açma kesinlikle ayrı izindir.
    private static readonly string[] StagePermissions = [CrmModule.Permissions.OpportunitiesChangeStage, CrmModule.Permissions.OpportunitiesWrite];
    private static readonly string[] ClosePermissions = [CrmModule.Permissions.OpportunitiesClose, CrmModule.Permissions.OpportunitiesWrite];
    private static readonly string[] ReopenPermissions = [CrmModule.Permissions.OpportunitiesReopen];
    private static readonly string[] WritePermissions = [CrmModule.Permissions.OpportunitiesWrite];

    public async Task<RecordResult<OpportunityActionResult>> ChangeStageAsync(
        Guid id, long version, string stage, string? note, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, StagePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, StagePermissions, cancellationToken);
        }

        if (OpportunityRules.CanChangeStage(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
        var target = list.Find(stage?.Trim() ?? string.Empty);
        if (target is null || !target.Active)
        {
            return Invalid("Listede olmayan ya da kullanılmayan aşama.", "stage");
        }

        if (target.Category != CrmModule.StageCategories.Open)
        {
            return Invalid("Kazanıldı/kaybedildi aşamasına geçmek için fırsatı kapatın (kazan / kaybet).", "stage");
        }

        // Aynı aşamaya geçiş değişiklik üretmez (çift tıklama / yinelenen istek tarihçeyi çoğaltmaz).
        if (string.Equals(Text(opp, "stage"), target.Key, StringComparison.Ordinal))
        {
            return Done(opp);
        }

        return await UpdateAsync(opp, version, StagePermissions, Trim(note), OpportunityEventReasons.StageChanged, new JsonObject
        {
            ["stage"] = target.Key,
        }, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> WinAsync(
        Guid id, long version, WinOpportunityRequest request, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, ClosePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, ClosePermissions, cancellationToken);
        }

        if (OpportunityRules.CanClose(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        // Firma kazanmayı bir onay sürecine bağladıysa onaylanmamış fırsat kazanıldı yapılamaz (ADR-0027 Ç6a.3).
        if (await approvalGate.BlockReasonAsync(new RecordRef(Key, opp.Id), CrmModule.ApprovalCommands.Win, cancellationToken) is { } awaiting)
        {
            return Invalid(awaiting, "approval");
        }

        var today = await TodayAsync(cancellationToken);
        var closeDate = request.ActualCloseDate ?? today;
        if (closeDate > today)
        {
            return Invalid("Gerçek kapanış tarihi gelecekte olamaz.", "actualCloseDate");
        }

        if (request.WonCurrency is { } code && (code.Trim().Length != Opportunity.CurrencyLength || !code.Trim().All(char.IsAsciiLetter)))
        {
            return Invalid("Üç harfli para birimi kodu girin (ör. TRY).", "wonCurrency");
        }

        if (request.WonAmount is < 0)
        {
            return Invalid("Kazanılan tutar negatif olamaz.", "wonAmount");
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
        var stage = request.Stage?.Trim() is { Length: > 0 } requested
            ? list.Find(requested)
            : list.Values.FirstOrDefault(v => v.Active && v.Category == CrmModule.StageCategories.Won);
        if (stage is null || !stage.Active || stage.Category != CrmModule.StageCategories.Won)
        {
            return Invalid("Kazanıldı kategorisinde etkin bir aşama bulunamadı.", "stage");
        }

        var values = new JsonObject
        {
            ["status"] = OpportunityStatus.Won,
            ["stage"] = stage.Key,
            ["actualCloseDate"] = closeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["winReason"] = Trim(request.Reason),
            ["handoverNote"] = Trim(request.HandoverNote),
            ["statusReason"] = null,
        };

        // Tutar hassas alandır: yalnızca açıkça verildiyse yazılır; verilmezse depo fırsat tutarını varsayılan alır.
        if (request.WonAmount is { } amount)
        {
            values["wonAmount"] = amount;
            values["wonCurrency"] = request.WonCurrency?.Trim().ToUpperInvariant() ?? Text(opp, "currency");
        }
        else if (request.WonCurrency is { } currency)
        {
            values["wonCurrency"] = currency.Trim().ToUpperInvariant();
        }

        return await CloseAsync(opp, version, ClosePermissions, null, OpportunityEventReasons.Won, values, CrmModule.Events.OpportunityWon, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> LoseAsync(
        Guid id, long version, LoseOpportunityRequest request, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, ClosePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, ClosePermissions, cancellationToken);
        }

        if (OpportunityRules.CanClose(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Invalid("Kaybetme nedeni zorunludur.", "lossReason");
        }

        var reasons = await choiceLists.GetAsync(CrmModule.Lists.OpportunityLossReason, cancellationToken);
        var reason = reasons.Find(request.Reason.Trim());
        if (reason is null || !reason.Active)
        {
            return Invalid("Listede olmayan ya da kullanılmayan kaybetme nedeni.", "lossReason");
        }

        // "Diğer" seçilirse açıklama istenir.
        if (reason.Category == CrmModule.LossCategories.Other && string.IsNullOrWhiteSpace(request.Note))
        {
            return Invalid("\"Diğer\" nedeni için açıklama girin.", "lostReason");
        }

        if (request.WinningCompetitorId is { } competitorId)
        {
            // Kazanan rakip bu fırsatın rakiplerinden biri olmalı ve nedeni rakibe kaybetmek olmalı; vazgeçme / mevcut çözüm rakibe kayıp değildir.
            if (reason.Category != CrmModule.LossCategories.Competitor)
            {
                return Invalid("Kazanan rakip yalnızca \"rakibe kaybedildi\" kategorisindeki nedenlerle belirtilir.", "winningCompetitorId");
            }

            var competitors = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.OpportunityCompetitor, 1, RecordQuery.MaxPageSize, null,
                new Dictionary<string, string> { ["opportunityId"] = id.ToString() }), cancellationToken);
            if (!competitors.IsSuccess || competitors.Value!.Items.All(c => c.Id != competitorId))
            {
                return Invalid("Rakip bu fırsatın rakipleri arasında değil.", "winningCompetitorId");
            }
        }

        var today = await TodayAsync(cancellationToken);
        if (request.RecontactDate is { } recontact && recontact <= today)
        {
            return Invalid("Yeniden iletişim tarihi gelecekte olmalıdır.", "recontactDate");
        }

        if (request.CreateFollowUp && request.RecontactDate is null)
        {
            return Invalid("Takip aktivitesi için yeniden iletişim tarihi girin.", "recontactDate");
        }

        if (request.CreateFollowUp && !await access.HasPermissionAsync(CrmModule.Permissions.ActivitiesWrite, cancellationToken))
        {
            return Invalid("Takip aktivitesi oluşturmak için aktivite yazma izni gerekir.", "createFollowUp");
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
        var stage = list.Values.FirstOrDefault(v => v.Active && v.Category == CrmModule.StageCategories.Lost);
        if (stage is null)
        {
            return Invalid("Kaybedildi kategorisinde etkin bir aşama bulunamadı.", "stage");
        }

        var result = await CloseAsync(opp, version, ClosePermissions, null, OpportunityEventReasons.Lost, new JsonObject
        {
            ["status"] = OpportunityStatus.Lost,
            ["stage"] = stage.Key,
            ["actualCloseDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["lossReason"] = reason.Key,
            ["lostReason"] = Trim(request.Note),
            ["winningCompetitorId"] = request.WinningCompetitorId?.ToString(),
            ["recontactDate"] = request.RecontactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["statusReason"] = null,
        }, CrmModule.Events.OpportunityLost, cancellationToken);
        if (!result.IsSuccess || !request.CreateFollowUp || request.RecontactDate is not { } due)
        {
            return result;
        }

        // Takip aktivitesi ayrı bir yazımdır (aktivite boru hattı): başarısız olsa bile kayıp geri alınmaz, uyarı döner.
        var followUp = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null, new JsonObject
        {
            ["type"] = "FollowUp",
            ["subject"] = $"Yeniden iletişim: {Text(opp, "title")}",
            ["dueAt"] = new DateTimeOffset(due.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
            ["opportunityId"] = id.ToString(),
            ["companyId"] = Text(opp, "companyId"),
        }), cancellationToken);
        return RecordResult<OpportunityActionResult>.Ok(followUp.IsSuccess
            ? result.Value! with { FollowUpActivityId = followUp.Value!.Id, OpenActivities = result.Value.OpenActivities + 1 }
            : result.Value! with { FollowUpWarning = $"Fırsat kaybedildi, ancak takip aktivitesi oluşturulamadı: {followUp.Error!.Message}" });
    }

    public async Task<RecordResult<OpportunityActionResult>> CancelAsync(
        Guid id, long version, string reason, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, ClosePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, ClosePermissions, cancellationToken);
        }

        if (OpportunityRules.CanClose(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("İptal gerekçesi zorunludur.", "statusReason");
        }

        // İptal edilen fırsat son açık aşamasını korur (aşama kategorisi açık kalır); kaybedilmiş sayılmaz.
        return await CloseAsync(opp, version, ClosePermissions, reason.Trim(), OpportunityEventReasons.Cancelled, new JsonObject
        {
            ["status"] = OpportunityStatus.Cancelled,
            ["statusReason"] = reason.Trim(),
            ["actualCloseDate"] = (await TodayAsync(cancellationToken)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, null, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> HoldAsync(
        Guid id, long version, string reason, DateOnly reviewDate, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, StagePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, StagePermissions, cancellationToken);
        }

        if (OpportunityRules.CanHold(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Beklemeye alma gerekçesi zorunludur.", "statusReason");
        }

        if (reviewDate <= await TodayAsync(cancellationToken))
        {
            return Invalid("Yeniden değerlendirme tarihi gelecekte olmalıdır.", "holdReviewDate");
        }

        return await UpdateAsync(opp, version, StagePermissions, reason.Trim(), OpportunityEventReasons.Held, new JsonObject
        {
            ["status"] = OpportunityStatus.OnHold,
            ["statusReason"] = reason.Trim(),
            ["holdReviewDate"] = reviewDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> ResumeAsync(Guid id, long version, string? note, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, StagePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, StagePermissions, cancellationToken);
        }

        return OpportunityRules.CanResume(Status(opp)) is { } blocked
            ? Invalid(blocked, "status")
            : await UpdateAsync(opp, version, StagePermissions, Trim(note), OpportunityEventReasons.Resumed, new JsonObject
            {
                ["status"] = OpportunityStatus.Open,
                ["statusReason"] = null,
                ["holdReviewDate"] = null,
            }, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> ReopenAsync(
        Guid id, long version, ReopenOpportunityRequest request, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, ReopenPermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, ReopenPermissions, cancellationToken);
        }

        if (OpportunityRules.CanReopen(Status(opp)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Invalid("Yeniden açma gerekçesi zorunludur.", "statusReason");
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
        var stage = list.Find(request.Stage?.Trim() ?? string.Empty);
        if (stage is null || !stage.Active || stage.Category != CrmModule.StageCategories.Open)
        {
            return Invalid("Yeniden açılan fırsat için etkin, açık bir aşama seçin.", "stage");
        }

        if (request.ExpectedCloseDate < await TodayAsync(cancellationToken))
        {
            return Invalid("Tahmini kapanış tarihi geçmişte olamaz.", "expectedCloseDate");
        }

        if (request.Probability is < 0 or > 100)
        {
            return Invalid("Olasılık 0 ile 100 arasında olmalıdır.", "probability");
        }

        var values = new JsonObject
        {
            ["status"] = OpportunityStatus.Open,
            ["stage"] = stage.Key,
            ["statusReason"] = null,
            ["expectedCloseDate"] = request.ExpectedCloseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        // Olasılık hassas alandır: verildiyse elle girilmiş sayılır; verilmezse aşamanın varsayılanı atanır.
        if (request.Probability is { } probability)
        {
            values["probability"] = probability;
            values["probabilityManual"] = true;
        }

        return await UpdateAsync(opp, version, ReopenPermissions, request.Reason.Trim(), OpportunityEventReasons.Reopened, values, cancellationToken);
    }

    public async Task<RecordResult<OpportunityActionResult>> RecordInteractionAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, WritePermissions, cancellationToken) is not { Record: { } opp })
        {
            return await FailAsync(id, WritePermissions, cancellationToken);
        }

        var current = Text(opp, "lastInteractionAt") is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        if (current is { } existing && existing >= at)
        {
            return Done(opp);
        }

        return await UpdateAsync(opp, opp.Version, WritePermissions, null, OpportunityEventReasons.Interaction, new JsonObject
        {
            ["lastInteractionAt"] = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<OpportunityHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        // Okuma izni ve veri kapsamı boru hattında uygulanır: fırsatı göremeyen tarihçesini de göremez.
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<OpportunityHistoryItem>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var showAmounts = await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken);
        var rows = await historyReader.ForAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<OpportunityHistoryItem>>.Ok([.. rows.Select(h => new OpportunityHistoryItem(
            h.Id, h.Kind, h.At, h.ChangedBy, h.Cycle, h.FromStage, h.ToStage, h.FromStatus, h.ToStatus, h.FromCloseDate, h.ToCloseDate,
            h.Reason, showAmounts ? h.Details : null))]);
    }

    // ---- yardımcılar

    private async Task<(RecordData? Record, bool Allowed)> LoadAsync(Guid id, string[] permissions, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!result.IsSuccess)
        {
            return (null, false);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        return permissions.Any(granted.Contains) ? (result.Value, true) : (null, false);
    }

    // Kayıt yok/kapsam dışı → NotFound; var ama eylem izni yok → PermissionDenied.
    private async Task<RecordResult<OpportunityActionResult>> FailAsync(Guid id, string[] permissions, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        return result.IsSuccess
            ? RecordResult<OpportunityActionResult>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için şu izinlerden biri gerekir: {string.Join(", ", permissions)}.")
            : RecordResult<OpportunityActionResult>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    private async Task<RecordResult<OpportunityActionResult>> UpdateAsync(
        RecordData opp, long version, string[] permissions, string? reason, string eventReason, JsonObject values, CancellationToken cancellationToken)
    {
        RecordResult<RecordData> result;
        using (gate.Open(permissions, reason))
        {
            result = await pipeline.UpdateAsync(new RecordCommand(Key, opp.Id, values, version) { Reason = eventReason }, cancellationToken);
        }

        return result.IsSuccess
            ? Done(result.Value!)
            : RecordResult<OpportunityActionResult>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    // Kapanış: kayıt kapanır; açık aktiviteler silinmez (açık kalır) ve sayıları sonuçta bildirilir. Kazanma ve kaybetme iş olayını
    // (ADR-0039) kayıt değişikliğiyle aynı işlemde yayınlar: ikisi birlikte yazılır ya da hiçbiri; iptalin iş olayı yoktur.
    private async Task<RecordResult<OpportunityActionResult>> CloseAsync(
        RecordData opp, long version, string[] permissions, string? reason, string eventReason, JsonObject values, string? businessEvent,
        CancellationToken cancellationToken)
    {
        var result = await pipeline.RunAtomicAsync(async ct =>
        {
            var updated = await UpdateAsync(opp, version, permissions, reason, eventReason, values, ct);
            if (updated.IsSuccess && businessEvent is not null)
            {
                var closed = updated.Value!.Opportunity;
                await events.PublishAsync(new BusinessEvent(businessEvent, closed.Id, Payload(closed, businessEvent)) { RecordVersion = closed.Version }, ct);
            }

            return updated;
        }, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        var open = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Activity, 1, 1, null,
            new Dictionary<string, string> { ["opportunityId"] = opp.Id.ToString(), ["open"] = "true" }), cancellationToken);
        return RecordResult<OpportunityActionResult>.Ok(result.Value! with { OpenActivities = open.IsSuccess ? open.Value!.TotalCount : 0 });
    }

    // İş olayının payload'u: beyan edilmiş alanlar, komutun döndürdüğü (çağıranın görebildiği) değerlerle. Tutar iznine sahip olmayan
    // çağıranın kazandırdığı fırsatta wonAmount zaten gizlidir; olayda da yoktur.
    private static JsonObject Payload(RecordData closed, string businessEvent)
    {
        var fields = businessEvent == CrmModule.Events.OpportunityWon ? CrmModule.Events.OpportunityWonFields : CrmModule.Events.OpportunityLostFields;
        var payload = new JsonObject();
        foreach (var field in fields)
        {
            if (closed.Values.TryGetPropertyValue(field, out var value) && value is not null)
            {
                payload[field] = value.DeepClone();
            }
        }

        return payload;
    }

    private static RecordResult<OpportunityActionResult> Done(RecordData opp) =>
        RecordResult<OpportunityActionResult>.Ok(new OpportunityActionResult(opp));

    /// <summary>Bugünün tarihi firmanın saat diliminde (raporlarla aynı politika); UTC günü gece 00:00–03:00 (İstanbul) arasında bir gün geri kalırdı.</summary>
    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        ReportPeriods.Today(time.GetUtcNow(), (await reportingPolicy.GetAsync(cancellationToken)).TimeZone);

    private static string Status(RecordData opp) => Text(opp, "status") ?? OpportunityStatus.Open;

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static RecordResult<OpportunityActionResult> Invalid(string message, string field) =>
        RecordResult<OpportunityActionResult>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
