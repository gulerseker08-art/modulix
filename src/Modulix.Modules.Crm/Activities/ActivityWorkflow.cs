using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Activities;

/// <summary>"Sonraki adım": aktivite tamamlanırken aynı işlemde açılan, önceki aktiviteye bağlanan yeni aktivite.</summary>
public sealed record NextStep(string Subject, string? Type = null, DateTimeOffset? DueAt = null, Guid? OwnerId = null);

/// <param name="Outcome">Sonuç kodu; boşsa ailenin varsayılanı (arama/toplantı: Reached, e-posta: Sent, görev: yok).</param>
/// <param name="Note">Sonuç açıklaması.</param>
/// <param name="CompletedAt">Gerçekleşme anı (geçmişe dönük kayıt); boşsa şimdi.</param>
public sealed record CompleteActivityCommand(
    string? Outcome = null, string? Note = null, DateTimeOffset? CompletedAt = null, int? ActualMinutes = null, NextStep? NextStep = null);

/// <summary>Yeni zaman bilgisinin tamamı (kısmi değil): tüm gün ya da saatli blok, isteğe bağlı son tarih. Gerekçe zorunludur.</summary>
/// <param name="AllowConflict">Aynı sorumlunun çakışan aktivitesi varsa yine de planla.</param>
public sealed record RescheduleCommand(
    string Reason, DateTimeOffset? DueAt = null, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null,
    bool IsAllDay = false, DateOnly? StartDate = null, DateOnly? EndDate = null, bool AllowConflict = false);

public sealed record ActivityCompletion(RecordData Activity, RecordData? NextStep);

/// <summary>
/// Aktivite yaşam döngüsü eylemleri (ADR-0015). Tek yazma yolu: eylem iznine bağlı iş akışı kapısı açılır ve kayıt boru hattı
/// üzerinden güncelleme yapılır; böylece kapsam, özel aktivite gizliliği, audit (eski/yeni değer, gerekçe) ve olay
/// (neden: <c>activity.*</c>) otomatik işlenir. Genel kayıt uçları durumu, tamamlanmayı ve sorumluyu değiştiremez. Her eylem kaydın
/// sürümünü ister (eşzamanlı güncelleme sessizce ezilmez). Yapay zekâ araçları da bu servisi kullanıcının izniyle çağırır.
/// </summary>
public interface IActivityWorkflow
{
    /// <summary>Görevi "Devam ediyor"a alır (<c>crm.activities.write</c>).</summary>
    Task<RecordResult<RecordData>> StartAsync(Guid id, long version, CancellationToken cancellationToken);

    /// <summary>Görevi nedenle bloke eder.</summary>
    Task<RecordResult<RecordData>> BlockAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    /// <summary>Bloke görevin engelini kaldırır; görev açık duruma döner.</summary>
    Task<RecordResult<RecordData>> UnblockAsync(Guid id, long version, CancellationToken cancellationToken);

    /// <summary>
    /// Tamamlar. Duruma etkisi aileye göre: görev Tamamlandı; arama Gerçekleştirildi ya da (sonuç NoAnswer) Ulaşılamadı;
    /// toplantı/ziyaret Gerçekleşti ya da (sonuç NoAnswer) Katılım olmadı; e-posta Gönderildi (elle kayıt: teslim ya da okundu
    /// anlamına GELMEZ). Sonraki adım verilmişse aynı işlemde yeni aktivite olarak açılır ve bu aktiviteye bağlanır.
    /// </summary>
    Task<RecordResult<ActivityCompletion>> CompleteAsync(Guid id, long version, CompleteActivityCommand command, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CancelAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    /// <summary>Kapanmış (tamamlanmış, iptal, ulaşılamadı, katılım olmadı) aktiviteyi gerekçeyle yeniden açar (<c>crm.activities.reopen</c>).</summary>
    Task<RecordResult<RecordData>> ReopenAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    /// <summary>Ertelemeyi tarih değişikliği, gerekçe ve sayaçla yapar; eski/yeni tarih audit geçmişindedir. Çakışma kontrolü yapar.</summary>
    Task<RecordResult<RecordData>> RescheduleAsync(Guid id, long version, RescheduleCommand command, CancellationToken cancellationToken);

    /// <summary>Birincil sorumluyu değiştirir. Kendine almak <c>activities.write</c>, başkasına atamak <c>activities.assign</c> ister.</summary>
    Task<RecordResult<RecordData>> ReassignAsync(Guid id, long version, Guid ownerId, CancellationToken cancellationToken);
}

internal sealed class ActivityWorkflow(
    IRecordPipeline pipeline,
    ActivityWorkflowGate gate,
    IChoiceLists choiceLists,
    ITenantContextAccessor tenant,
    TimeProvider time) : IActivityWorkflow
{
    private const string Key = CrmModule.Entities.Activity;
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public Task<RecordResult<RecordData>> StartAsync(Guid id, long version, CancellationToken cancellationToken) =>
        MoveAsync(id, version, ActivityStatus.InProgress, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Started,
            _ => new JsonObject { ["statusReason"] = null }, cancellationToken);

    public async Task<RecordResult<RecordData>> BlockAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(reason)
            ? Invalid("Bloke nedeni zorunludur.", "statusReason")
            : await MoveAsync(id, version, ActivityStatus.Blocked, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Blocked,
                _ => new JsonObject { ["statusReason"] = reason.Trim() }, cancellationToken);

    public Task<RecordResult<RecordData>> UnblockAsync(Guid id, long version, CancellationToken cancellationToken) =>
        MoveAsync(id, version, ActivityStatus.Open, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Unblocked,
            _ => new JsonObject { ["statusReason"] = null }, cancellationToken, requireFrom: ActivityStatus.Blocked);

    public async Task<RecordResult<RecordData>> CancelAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(reason)
            ? Invalid("İptal gerekçesi zorunludur.", "statusReason")
            : await MoveAsync(id, version, ActivityStatus.Cancelled, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Cancelled,
                _ => new JsonObject { ["statusReason"] = reason.Trim() }, cancellationToken);

    public async Task<RecordResult<RecordData>> ReopenAsync(Guid id, long version, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Yeniden açma gerekçesi zorunludur.", "statusReason");
        }

        if (await LoadAsync(id, cancellationToken) is not { Activity: { } activity, Family: var family, Error: null })
        {
            return await FailAsync(id, cancellationToken);
        }

        var status = ActivityRules.Normalize(family, Text(activity, "status"));
        if (ActivityStatus.IsOpen(status))
        {
            return Invalid("Aktivite zaten açık.", "status");
        }

        var initial = ActivityRules.InitialStatus(family);
        if (ActivityRules.CanMove(family, status, initial) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        // Tamamlanma ve sonuç depoda temizlenir (açık durumda tamamlanma tutulmaz); gerekçe audit'te kalır.
        return await UpdateAsync(activity, version, CrmModule.Permissions.ActivitiesReopen, ActivityEventReasons.Reopened, new JsonObject
        {
            ["status"] = initial,
            ["statusReason"] = reason.Trim(),
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> RescheduleAsync(Guid id, long version, RescheduleCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Invalid("Erteleme gerekçesi zorunludur.", "statusReason");
        }

        if (await LoadAsync(id, cancellationToken) is not { Activity: { } activity, Family: var family, Error: null })
        {
            return await FailAsync(id, cancellationToken);
        }

        if (!ActivityStatus.IsOpen(ActivityRules.Normalize(family, Text(activity, "status"))))
        {
            return Invalid("Kapanmış aktivite ertelenemez; önce yeniden açın.", "status");
        }

        if (ActivityRules.ValidateSchedule(command.IsAllDay, command.StartAt, command.EndAt, command.StartDate, command.EndDate) is { } invalid)
        {
            return Invalid(invalid, command.IsAllDay ? "startDate" : "endAt");
        }

        if (!command.AllowConflict && command.StartAt is { } start && command.EndAt is { } end
            && await ConflictCountAsync(activity, start, end, cancellationToken) is var conflicts and > 0)
        {
            return Invalid($"Bu saatte aynı sorumlunun {conflicts} çakışan aktivitesi var.", "conflict");
        }

        static string? Instant(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        static string? Day(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return await UpdateAsync(activity, version, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Rescheduled, new JsonObject
        {
            ["dueAt"] = Instant(command.DueAt),
            ["startAt"] = command.IsAllDay ? null : Instant(command.StartAt),
            ["endAt"] = command.IsAllDay ? null : Instant(command.EndAt),
            ["isAllDay"] = command.IsAllDay,
            ["startDate"] = command.IsAllDay ? Day(command.StartDate) : null,
            ["endDate"] = command.IsAllDay ? Day(command.EndDate) : null,
            ["statusReason"] = command.Reason.Trim(),
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> ReassignAsync(Guid id, long version, Guid ownerId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, cancellationToken) is not { Activity: { } activity, Family: var family, Error: null })
        {
            return await FailAsync(id, cancellationToken);
        }

        if (!ActivityStatus.IsOpen(ActivityRules.Normalize(family, Text(activity, "status"))))
        {
            return Invalid("Kapanmış aktivite yeniden atanamaz.", "status");
        }

        // Aynı kişiye yeniden atama değişiklik üretmez: tekrar denemelerde bildirim ve audit çoğalmaz.
        if (Text(activity, "ownerId") == ownerId.ToString())
        {
            return RecordResult<RecordData>.Ok(activity);
        }

        var permission = ownerId == tenant.Current?.UserId ? CrmModule.Permissions.ActivitiesWrite : CrmModule.Permissions.ActivitiesAssign;
        return await UpdateAsync(activity, version, permission, ActivityEventReasons.Reassigned,
            new JsonObject { ["ownerId"] = ownerId.ToString() }, cancellationToken);
    }

    public async Task<RecordResult<ActivityCompletion>> CompleteAsync(
        Guid id, long version, CompleteActivityCommand command, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, cancellationToken) is not { Activity: { } activity, Family: var family, Error: null })
        {
            var failed = await FailAsync(id, cancellationToken);
            return RecordResult<ActivityCompletion>.Fail(failed.Error!.Code, failed.Error.Message, failed.Error.FieldErrors);
        }

        var outcome = command.Outcome ?? DefaultOutcome(family);
        if (family == ActivityFamily.Task ? outcome is not null : outcome is null || !AllowedOutcomes(family).Contains(outcome))
        {
            return InvalidCompletion(family == ActivityFamily.Task ? "Görevin sonuç kodu olmaz." : $"Sonuç şunlardan biri olmalıdır: {string.Join(", ", AllowedOutcomes(family))}.", "outcome");
        }

        var now = time.GetUtcNow();
        var completedAt = command.CompletedAt ?? now;
        if (completedAt > now + FutureTolerance)
        {
            return InvalidCompletion("Tamamlanma zamanı gelecekte olamaz.", "completedAt");
        }

        var target = ActivityRules.CompletionTarget(family, outcome);
        var status = ActivityRules.Normalize(family, Text(activity, "status"));
        if (ActivityRules.CanMove(family, status, target) is { } blocked)
        {
            return InvalidCompletion(status == ActivityStatus.Blocked ? "Bloke görev tamamlanamaz; önce engeli kaldırın." : blocked, "status");
        }

        if (command.NextStep is { } step && string.IsNullOrWhiteSpace(step.Subject))
        {
            return InvalidCompletion("Sonraki adımın konusu zorunludur.", "nextStep");
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            var values = new JsonObject
            {
                ["status"] = target,
                ["completedAt"] = completedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["outcome"] = outcome,
                ["statusReason"] = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(),
            };
            if (command.ActualMinutes is { } minutes)
            {
                values["actualMinutes"] = minutes;
            }

            var done = await UpdateAsync(activity, version, CrmModule.Permissions.ActivitiesWrite, ActivityEventReasons.Completed, values, ct);
            if (!done.IsSuccess)
            {
                return RecordResult<ActivityCompletion>.Fail(done.Error!.Code, done.Error.Message, done.Error.FieldErrors);
            }

            if (command.NextStep is null)
            {
                return RecordResult<ActivityCompletion>.Ok(new ActivityCompletion(done.Value!, null));
            }

            var next = await CreateNextStepAsync(done.Value!, command.NextStep, ct);
            return next.IsSuccess
                ? RecordResult<ActivityCompletion>.Ok(new ActivityCompletion(done.Value!, next.Value))
                : RecordResult<ActivityCompletion>.Fail(next.Error!.Code, next.Error.Message, next.Error.FieldErrors);
        }, cancellationToken);
    }

    // ---- Sonraki adım

    private async Task<RecordResult<RecordData>> CreateNextStepAsync(RecordData previous, NextStep step, CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["subject"] = step.Subject.Trim(),
            ["dueAt"] = step.DueAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["previousActivityId"] = previous.Id.ToString(),
            ["isPrivate"] = previous.Values["isPrivate"]?.GetValue<bool>() ?? false,
        };
        if (!string.IsNullOrWhiteSpace(step.Type))
        {
            values["type"] = step.Type.Trim();
        }

        // Aynı iş kaydına bağlı kalır: doğrudan ilişkiler kopyalanır (görünürlük yeniden doğrulanır), sorumlu verilmezse aynı kişi.
        foreach (var name in new[] { "companyId", "contactId", "opportunityId", "leadId" })
        {
            values[name] = previous.Values[name]?.DeepClone();
        }

        values["ownerId"] = step.OwnerId?.ToString() ?? previous.Values["ownerId"]?.DeepClone();

        RecordResult<RecordData> created;
        using (gate.Open(CrmModule.Permissions.ActivitiesWrite))
        {
            created = await pipeline.CreateAsync(new RecordCommand(Key, null, values) { Reason = ActivityEventReasons.Completed }, cancellationToken);
        }

        if (!created.IsSuccess)
        {
            return created;
        }

        // Genel ilişki satırları da yeni adıma taşınır; her biri kendi görünürlük denetiminden geçer.
        var links = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.ActivityLink, 1, RecordQuery.MaxPageSize, null,
            new Dictionary<string, string> { ["activityId"] = previous.Id.ToString() }), cancellationToken);
        foreach (var link in links.IsSuccess ? links.Value!.Items : [])
        {
            var copy = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.ActivityLink, null, new JsonObject
            {
                ["activityId"] = created.Value!.Id.ToString(),
                ["entityKey"] = link.Values["entityKey"]?.DeepClone(),
                ["recordId"] = link.Values["recordId"]?.DeepClone(),
                ["isPrimary"] = link.Values["isPrimary"]?.DeepClone(),
            }), cancellationToken);
            if (!copy.IsSuccess)
            {
                return RecordResult<RecordData>.Fail(copy.Error!.Code, copy.Error.Message, copy.Error.FieldErrors);
            }
        }

        return created;
    }

    // ---- Yardımcılar

    private static string? DefaultOutcome(string family) => family switch
    {
        ActivityFamily.Call or ActivityFamily.Meeting or ActivityFamily.Visit => CrmModule.ActivityOutcomes.Reached,
        ActivityFamily.Email => CrmModule.ActivityOutcomes.Sent,
        _ => null,
    };

    private static string[] AllowedOutcomes(string family) => family switch
    {
        ActivityFamily.Email => [CrmModule.ActivityOutcomes.Sent],
        _ => [CrmModule.ActivityOutcomes.Reached, CrmModule.ActivityOutcomes.NoAnswer],
    };

    private async Task<RecordResult<RecordData>> MoveAsync(
        Guid id, long version, string target, string permission, string reason, Func<string, JsonObject> extra,
        CancellationToken cancellationToken, string? requireFrom = null)
    {
        if (await LoadAsync(id, cancellationToken) is not { Activity: { } activity, Family: var family, Error: null })
        {
            return await FailAsync(id, cancellationToken);
        }

        var status = ActivityRules.Normalize(family, Text(activity, "status"));
        if (requireFrom is not null && status != requireFrom)
        {
            return Invalid("Aktivite bloke değil.", "status");
        }

        if (ActivityRules.CanMove(family, status, target) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        var values = extra(status);
        values["status"] = target;
        return await UpdateAsync(activity, version, permission, reason, values, cancellationToken);
    }

    private async Task<int> ConflictCountAsync(RecordData activity, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (Text(activity, "ownerId") is not { } owner)
        {
            return 0;
        }

        // Pencere sorumlunun o saat aralığıdır; özel aktiviteler de meşguliyet olarak sayılır (içerikleri okunmaz).
        var range = $"{start.ToUniversalTime():O}|{end.ToUniversalTime():O}|{start.UtcDateTime:yyyy-MM-dd}|{end.UtcDateTime:yyyy-MM-dd}";
        var result = await pipeline.QueryAsync(new RecordQuery(Key, 1, RecordQuery.MaxPageSize, null, new Dictionary<string, string>
        {
            ["ownerId"] = owner,
            ["open"] = "true",
            ["range"] = range,
        }), cancellationToken);
        return result.IsSuccess
            ? result.Value!.Items.Count(i => i.Id != activity.Id && i.Values["startAt"] is not null && i.Values["endAt"] is not null)
            : 0;
    }

    private async Task<(RecordData? Activity, string Family, RecordError? Error)> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!result.IsSuccess)
        {
            return (null, ActivityFamily.Task, result.Error);
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.ActivityType, cancellationToken);
        return (result.Value, ActivityFamily.Of(list.Find(Text(result.Value!, "type") ?? string.Empty)?.Category), null);
    }

    private async Task<RecordResult<RecordData>> FailAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        return result.IsSuccess
            ? RecordResult<RecordData>.Ok(result.Value!)
            : RecordResult<RecordData>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    private async Task<RecordResult<RecordData>> UpdateAsync(
        RecordData activity, long version, string permission, string reason, JsonObject values, CancellationToken cancellationToken)
    {
        using var _ = gate.Open(permission);
        return await pipeline.UpdateAsync(new RecordCommand(Key, activity.Id, values, version) { Reason = reason }, cancellationToken);
    }

    private static string? Text(RecordData record, string name) => record.Values[name]?.GetValue<string>();

    private static RecordResult<RecordData> Invalid(string message, string field) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    private static RecordResult<ActivityCompletion> InvalidCompletion(string message, string field) =>
        RecordResult<ActivityCompletion>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
