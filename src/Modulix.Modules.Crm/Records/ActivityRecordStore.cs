using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Aktivite kayıt deposu (ADR-0015). Alan düzenlemesi <c>crm.activities.write</c>; durum, gerekçe, tamamlanma ve sonuç değişiklikleri
/// ile sorumlu değişimi eylem kapısı (<see cref="ActivityWorkflowGate"/>) açıkken ilgili eylemin iziyle yapılır. Tamamlanma ve sonuç
/// oluştururken verilebilir (geçmiş tarihli etkileşim kaydı); sonra yalnızca <c>IActivityWorkflow</c> değiştirir. Veri kapsamı
/// sorumluya ve katılımcılığa göredir; özel aktivitenin ayrıntısı kapsamdaki herkese değil, yalnızca yetkililere açıktır.
/// </summary>
internal sealed class ActivityRecordStore(CrmStoreContext context) : CrmRecordStore<Activity>(context), IRecordStore
{
    private const int MaxTimedBlockDays = 14;
    private const int MaxAllDaySpanDays = 366;

    public override string EntityKey => CrmModule.Entities.Activity;

    protected override string ReadPermission => CrmModule.Permissions.ActivitiesRead;

    protected override string WritePermission => CrmModule.Permissions.ActivitiesWrite;

    protected override bool GateOpen => Context.ActivityGate.IsOpen;

    /// <summary>Kapı açıkken güncelleme yalnızca eylemin iznini ister; kapalıyken sıradan güncelleme izni geçerlidir.</summary>
    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Update && Context.ActivityGate.Permission is { } permission ? [permission] : null;

    protected override IReadOnlyList<RecordField<Activity>> Fields { get; } =
    [
        Field.ListChoice<Activity>("type", CrmModule.Lists.ActivityType, a => a.Type, (a, v) => a.Type = v!),
        Field.Text<Activity>("subject", Activity.SubjectMaxLength, a => a.Subject, (a, v) => a.Subject = v!, required: true),
        Field.Text<Activity>("notes", Company.NotesMaxLength, a => a.Notes, (a, v) => a.Notes = v),
        Field.Text<Activity>("status", 20, a => a.Status, (a, v) => a.Status = v!).AsSystem(),
        Field.Text<Activity>("statusReason", Activity.ReasonMaxLength, a => a.StatusReason, (a, v) => a.StatusReason = v).AsSystem(),
        Field.Choice<Activity>("priority", ActivityPriority.All, a => a.Priority, (a, v) => a.Priority = v),
        Field.Time<Activity>("dueAt", a => a.DueAt, (a, v) => a.DueAt = v),
        Field.Time<Activity>("startAt", a => a.StartAt, (a, v) => a.StartAt = v),
        Field.Time<Activity>("endAt", a => a.EndAt, (a, v) => a.EndAt = v),
        Field.Flag<Activity>("isAllDay", a => a.IsAllDay, (a, v) => a.IsAllDay = v),
        Field.Date<Activity>("startDate", a => a.StartDate, (a, v) => a.StartDate = v),
        Field.Date<Activity>("endDate", a => a.EndDate, (a, v) => a.EndDate = v),
        Field.Text<Activity>("timeZone", Activity.TimeZoneMaxLength, a => a.TimeZone, (a, v) => a.TimeZone = v),
        Field.Text<Activity>("location", Activity.LocationMaxLength, a => a.Location, (a, v) => a.Location = v),
        Field.Url<Activity>("onlineMeetingUrl", Activity.UrlMaxLength, a => a.OnlineMeetingUrl, (a, v) => a.OnlineMeetingUrl = v),
        Field.Flag<Activity>("isPrivate", a => a.IsPrivate, (a, v) => a.IsPrivate = v),
        Field.Minutes<Activity>("estimatedMinutes", Activity.MaxMinutes, a => a.EstimatedMinutes, (a, v) => a.EstimatedMinutes = v),
        Field.Minutes<Activity>("actualMinutes", Activity.MaxMinutes, a => a.ActualMinutes, (a, v) => a.ActualMinutes = v),
        // Tamamlanma ve sonuç: oluştururken serbest (geçmiş tarihli kayıt), sonra yalnızca eylem servisi.
        Field.Time<Activity>("completedAt", a => a.CompletedAt, (a, v) => a.CompletedAt = v).GatedUpdate(),
        Field.OptionalChoice<Activity>("outcome", CrmModule.ActivityOutcomes.All, a => a.Outcome, (a, v) => a.Outcome = v).GatedUpdate(),
        Field.Reference<Activity, Company>("companyId", "Firma bulunamadı.", a => a.CompanyId, (a, v) => a.CompanyId = v),
        Field.Reference<Activity, Contact>("contactId", "Kişi bulunamadı.", a => a.ContactId, (a, v) => a.ContactId = v),
        Field.Reference<Activity, Opportunity>("opportunityId", "Fırsat bulunamadı.", a => a.OpportunityId, (a, v) => a.OpportunityId = v),
        Field.Reference<Activity, Lead>("leadId", "Müşteri adayı bulunamadı.", a => a.LeadId, (a, v) => a.LeadId = v),
        Field.Owner<Activity>(OwnerField, a => a.OwnerId, (a, v) => a.OwnerId = v).GatedUpdate(),
        Field.Id<Activity>("createdBy", a => a.CreatedBy, (a, v) => a.CreatedBy = v).AsSystem(),
        Field.Text<Activity>("source", 20, a => a.Source, (a, v) => a.Source = v!).AsSystem(),
        Field.Count<Activity>("rescheduleCount", a => a.RescheduleCount, (a, v) => a.RescheduleCount = v).AsSystem(),
        Field.Id<Activity>("previousActivityId", a => a.PreviousActivityId, (a, v) => a.PreviousActivityId = v).AsSystem(),
        Field.Text<Activity>("externalSource", 50, a => a.ExternalSource, (a, v) => a.ExternalSource = v).AsSystem(),
        Field.Text<Activity>("externalId", Activity.ExternalIdMaxLength, a => a.ExternalId, (a, v) => a.ExternalId = v).AsSystem(),
    ];

    private static readonly string[] OpenStates = [.. ActivityStatus.OpenStates];

    /// <summary>
    /// Süzgeçler. İlişki süzgeçleri (<c>companyId</c>, <c>related</c>, <c>timeline</c>...) <see cref="AsyncFilters"/>'tadır: özel
    /// aktivitenin ilişkisi, ayrıntıyı görmeyen kullanıcıya hiçbir yoldan sızmasın diye ayrıntı görünürlüğünü de uygular.
    /// </summary>
    protected override IReadOnlyDictionary<string, RecordFilter<Activity>> Filters { get; } = new Dictionary<string, RecordFilter<Activity>>
    {
        // Oluşturulma anı aralığı (ana sayfa dönem karşılaştırması): alt sınır dahil, üst sınır hariç.
        ["createdSince"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(a => a.CreatedAt >= time) : null,
        ["createdBefore"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(a => a.CreatedAt < time) : null,
        // Gecikmiş: açık durumda ve son tarihi verilen andan önce. Tamamlanmış/iptal kayıt hiçbir zaman gecikmiş sayılmaz.
        ["overdue"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(a => OpenStates.Contains(a.Status) && a.DueAt != null && a.DueAt < time) : null,
        ["open"] = (q, v, _) => FilterValue.Bool(v) is { } open
            ? open ? q.Where(a => OpenStates.Contains(a.Status)) : q.Where(a => !OpenStates.Contains(a.Status))
            : null,
        ["dueBefore"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(a => a.DueAt != null && a.DueAt < time) : null,
        ["status"] = (q, v, _) => ActivityStatus.All.Contains(v) ? q.Where(a => a.Status == v) : null,
        ["priority"] = (q, v, _) => ActivityPriority.All.Contains(v) ? q.Where(a => a.Priority == v) : null,
        ["type"] = (q, v, _) => ChoiceValueKeys.IsValid(v) ? q.Where(a => a.Type == v) : null,
        ["previousActivityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.PreviousActivityId == id) : null,
        ["participant"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } user
            ? q.Where(a => ctx.Data.Set<ActivityParticipant>().Any(p => p.ActivityId == a.Id && p.UserId == user))
            : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(a => a.OwnerId == owner) : null,
        // Takvim penceresi "başlangıç/bitiş/başlangıçGünü/bitişGünü": saatli bloklar, tüm gün etkinlikleri ve yalnızca son tarihli işler.
        ["range"] = (q, v, _) => CalendarRange.Parse(v) is { } range ? q.Where(CalendarRange.Overlapping(range)) : null,
    };

    protected override IReadOnlyDictionary<string, AsyncRecordFilter<Activity>> AsyncFilters { get; } =
        new Dictionary<string, AsyncRecordFilter<Activity>>
        {
            ["companyId"] = (q, v, ctx, ct) => RelatedAsync(q, ctx, CrmModule.Entities.Company, v, rollUp: false, ct),
            ["contactId"] = (q, v, ctx, ct) => RelatedAsync(q, ctx, CrmModule.Entities.Contact, v, rollUp: false, ct),
            ["opportunityId"] = (q, v, ctx, ct) => RelatedAsync(q, ctx, CrmModule.Entities.Opportunity, v, rollUp: false, ct),
            ["leadId"] = (q, v, ctx, ct) => RelatedAsync(q, ctx, CrmModule.Entities.Lead, v, rollUp: false, ct),
            // related=crm.company:{id}  (başka modüllerin kayıt türleri de aynı biçimde)
            ["related"] = (q, v, ctx, ct) => RelatedPairAsync(q, ctx, v, rollUp: false, ct),
            // timeline=crm.company:{id}: firma için kişileri ve fırsatları da içeren birleşik akış
            ["timeline"] = (q, v, ctx, ct) => RelatedPairAsync(q, ctx, v, rollUp: true, ct),
        };

    private static Task<IQueryable<Activity>?> RelatedPairAsync(
        IQueryable<Activity> source, CrmStoreContext context, string value, bool rollUp, CancellationToken cancellationToken) =>
        value.Split(':', 2) is [{ Length: > 0 } key, var id]
            ? RelatedAsync(source, context, key, id, rollUp, cancellationToken)
            : Task.FromResult<IQueryable<Activity>?>(null);

    private static async Task<IQueryable<Activity>?> RelatedAsync(
        IQueryable<Activity> source, CrmStoreContext context, string entityKey, string value, bool rollUp, CancellationToken cancellationToken)
    {
        if (FilterValue.Id(value) is not { } id || ActivityVisibility.Related(context, entityKey, id, rollUp) is not { } predicate)
        {
            return null;
        }

        return (await ActivityVisibility.DetailVisibleAsync(source, context, cancellationToken)).Where(predicate);
    }

    /// <summary>Açık aktiviteler önce, en yakın tarihli üstte; tarihsizler sonda. Kapalılar en son kapananlar önde.</summary>
    protected override IOrderedQueryable<Activity> Order(IQueryable<Activity> source) =>
        source.OrderBy(a => OpenStates.Contains(a.Status) ? 0 : 1)
            .ThenBy(a => (a.DueAt ?? a.StartAt) == null)
            .ThenBy(a => a.DueAt ?? a.StartAt)
            .ThenByDescending(a => a.CompletedAt);

    // ---- Veri kapsamı: sorumlu ya da katılımcı.

    protected override IQueryable<Activity> ScopeToOwners(IQueryable<Activity> source, IReadOnlySet<Guid> owners) =>
        source.Where(ActivityVisibility.InScope(Context, owners));

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (await base.IsInScopeAsync(record, owners, cancellationToken))
        {
            return true;
        }

        var me = Context.CurrentUserId;
        return me is not null && await Context.Data.Set<ActivityParticipant>().AsNoTracking()
            .AnyAsync(p => p.ActivityId == record.Id && p.UserId == me, cancellationToken);
    }

    // ---- Özel aktivite: ayrıntı yalnızca sorumlu/oluşturan/katılımcı/izinli kullanıcıda; diğerleri meşguliyet görür.

    protected override async Task<IReadOnlySet<Guid>> HiddenDetailsAsync(IReadOnlyList<Activity> items, CancellationToken cancellationToken)
    {
        var hidden = new HashSet<Guid>();
        var privateItems = items.Where(a => a.IsPrivate).ToList();
        if (privateItems.Count == 0 || await ActivityVisibility.CanViewPrivateAsync(Context, cancellationToken))
        {
            return hidden;
        }

        var me = Context.CurrentUserId;
        var ids = privateItems.Select(a => a.Id).ToList();
        var participating = me is null
            ? []
            : await Context.Data.Set<ActivityParticipant>().AsNoTracking()
                .Where(p => ids.Contains(p.ActivityId) && p.UserId == me).Select(p => p.ActivityId).ToListAsync(cancellationToken);
        foreach (var item in privateItems.Where(a => a.OwnerId != me && a.CreatedBy != me && !participating.Contains(a.Id)))
        {
            hidden.Add(item.Id);
        }

        return hidden;
    }

    /// <summary>Meşguliyet görünümü: yalnızca zaman bilgisi ve sorumlu. Başlık, açıklama, ilişkiler, konum ve bağlantı verilmez.</summary>
    protected override RecordData Mask(Activity entity, RecordData full)
    {
        var values = new JsonObject
        {
            ["subject"] = "Meşgul",
            ["isPrivate"] = true,
            ["masked"] = true,
            ["status"] = full.Values["status"]?.DeepClone(),
            ["ownerId"] = full.Values["ownerId"]?.DeepClone(),
            ["priority"] = null,
        };
        foreach (var name in new[] { "dueAt", "startAt", "endAt", "isAllDay", "startDate", "endDate", "timeZone" })
        {
            values[name] = full.Values[name]?.DeepClone();
        }

        values[RecordFields.Custom] = new JsonObject();
        return new RecordData(full.EntityKey, full.Id, full.Version, values);
    }

    protected override async Task<IQueryable<Activity>> SearchAsync(IQueryable<Activity> source, string term, CancellationToken cancellationToken)
    {
        // Ayrıntıyı göremeyen kullanıcının araması özel aktivitenin başlığını eşleştirmez.
        var visible = await ActivityVisibility.DetailVisibleAsync(source, Context, cancellationToken);
        return visible.Where(a => a.Subject.Contains(term) || (a.Location != null && a.Location.Contains(term)));
    }

    protected override IQueryable<Activity> Search(IQueryable<Activity> source, string term) =>
        source.Where(a => a.Subject.Contains(term));

    // ---- Oluşturma ve doğrulama

    protected override Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Activity entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.CreatedBy = Context.CurrentUserId;
        entity.Source = ActivitySource.User;
        entity.Status = ActivityStatus.Open;
        return Task.FromResult((values, false));
    }

    protected override async Task AppliedAsync(Activity entity, JsonObject values, CancellationToken cancellationToken)
    {
        var stored = await Context.Data.Set<Activity>().AsNoTracking().Where(a => a.Id == entity.Id).Select(a => new
        {
            a.Type, a.Status, a.DueAt, a.StartAt, a.EndAt, a.IsAllDay, a.StartDate, a.EndDate,
            a.CompanyId, a.ContactId, a.OpportunityId, a.LeadId,
        }).SingleOrDefaultAsync(cancellationToken);

        var errors = new List<RecordFieldError>();
        var list = await Context.ChoiceLists.GetAsync(CrmModule.Lists.ActivityType, cancellationToken);
        var family = ActivityFamily.Of(list.Find(entity.Type)?.Category);

        // Durum ailenin durumu olmalı; tür başka aileye değişiyorsa yalnızca başlangıç durumundaki kayıtta mümkündür.
        if (stored is not null && stored.Type != entity.Type)
        {
            var oldFamily = ActivityFamily.Of(list.Find(stored.Type)?.Category);
            if (oldFamily != family && ActivityRules.Normalize(oldFamily, stored.Status) != ActivityRules.InitialStatus(oldFamily))
            {
                errors.Add(new RecordFieldError("type", "Başlamış ya da kapanmış bir aktivitenin türü başka bir aileye değiştirilemez."));
            }
        }

        if (stored is null || stored.Type != entity.Type)
        {
            entity.Status = ActivityRules.Normalize(family, entity.Status);
        }

        // Geçmiş tarihli etkileşim kaydı: oluştururken tamamlanma verilmişse durum tamamlanmış olur.
        if (stored is null && entity.CompletedAt is { } completedAt)
        {
            if (completedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                errors.Add(new RecordFieldError("completedAt", "Tamamlanma zamanı gelecekte olamaz."));
            }

            entity.Status = ActivityRules.CompletionTarget(family, entity.Outcome);
        }

        var closed = !ActivityStatus.IsOpen(entity.Status);
        var scheduleChanged = stored is not null && (stored.DueAt != entity.DueAt || stored.StartAt != entity.StartAt || stored.EndAt != entity.EndAt
            || stored.IsAllDay != entity.IsAllDay || stored.StartDate != entity.StartDate || stored.EndDate != entity.EndDate);

        if (ActivityRules.ValidateSchedule(entity.IsAllDay, entity.StartAt, entity.EndAt, entity.StartDate, entity.EndDate) is { } scheduleError)
        {
            errors.Add(new RecordFieldError(entity.IsAllDay ? "startDate" : "endAt", scheduleError));
        }
        else if (entity is { IsAllDay: false, StartAt: { } start, EndAt: { } end } && end - start > TimeSpan.FromDays(MaxTimedBlockDays))
        {
            errors.Add(new RecordFieldError("endAt", $"Zaman bloğu en fazla {MaxTimedBlockDays} gün olabilir."));
        }
        else if (entity is { IsAllDay: true, StartDate: { } startDate } && ActivityRules.DaySpan(startDate, entity.EndDate) > MaxAllDaySpanDays)
        {
            errors.Add(new RecordFieldError("endDate", $"Tüm gün etkinliği en fazla {MaxAllDaySpanDays} gün sürebilir."));
        }

        if (entity.TimeZone is { } zone && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            errors.Add(new RecordFieldError("timeZone", "Geçerli bir saat dilimi kimliği girin (ör. Europe/Istanbul)."));
        }

        if (entity.StartAt is not null && entity.TimeZone is null)
        {
            errors.Add(new RecordFieldError("timeZone", "Saatli etkinlik için saat dilimi zorunludur."));
        }

        // Kapanmış aktivitenin zamanı yeniden açılmadan değişmez: tamamlanmış geçmiş korunur.
        if (scheduleChanged && closed && stored is not null && !ActivityStatus.IsOpen(stored.Status))
        {
            errors.Add(new RecordFieldError("dueAt", "Kapanmış aktivitenin zamanı değiştirilemez; önce yeniden açın."));
        }
        else if (scheduleChanged)
        {
            // Erteleme sayılır: eski/yeni tarih audit geçmişinde durur, sayaç kaç kez ertelendiğini gösterir.
            entity.RescheduleCount++;
        }

        errors.AddRange(await LinkErrorsAsync(entity, stored is null ? null : new(stored.CompanyId, stored.ContactId, stored.OpportunityId, stored.LeadId), cancellationToken));

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        // Tamamlanma yalnızca kayıt tamamlanma durumundaysa durur (yeniden açılan kayıtta temizlenir).
        if (!ActivityRules.RecordsCompletion(entity.Status))
        {
            entity.CompletedAt = null;
            entity.Outcome = null;
        }
        else
        {
            entity.CompletedAt ??= DateTimeOffset.UtcNow;
        }
    }

    private sealed record Links(Guid? CompanyId, Guid? ContactId, Guid? OpportunityId, Guid? LeadId);

    /// <summary>
    /// Doğrudan ilişkiler değişmişse çağıranın görebildiği kayıtlar olmalıdır; erişilemeyen kayıt var olmayan kayıtla aynı cevabı alır.
    /// Değişmeyen ilişki yeniden gönderilirse denetlenmez (görmediğim kayda bağlı eski aktiviteyi düzenleyebilirim).
    /// </summary>
    private async Task<List<RecordFieldError>> LinkErrorsAsync(Activity entity, Links? stored, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var checks = new (string Field, string Entity, string Message, Guid? Value, Guid? Old)[]
        {
            ("companyId", CrmModule.Entities.Company, "Firma bulunamadı.", entity.CompanyId, stored?.CompanyId),
            ("contactId", CrmModule.Entities.Contact, "Kişi bulunamadı.", entity.ContactId, stored?.ContactId),
            ("opportunityId", CrmModule.Entities.Opportunity, "Fırsat bulunamadı.", entity.OpportunityId, stored?.OpportunityId),
            ("leadId", CrmModule.Entities.Lead, "Müşteri adayı bulunamadı.", entity.LeadId, stored?.LeadId),
        };

        foreach (var (field, entityKey, message, value, old) in checks)
        {
            if (value is { } id && id != old
                && !(Context.LinkTargets.FirstOrDefault(t => t.EntityKey == entityKey) is { } target && await target.IsVisibleAsync(id, cancellationToken)))
            {
                errors.Add(new RecordFieldError(field, message));
            }
        }

        await LookupGuard.AddContactCompanyErrorAsync(
            errors, Context, "contactId", entity.ContactId, entity.CompanyId, stored?.ContactId, stored?.CompanyId, cancellationToken);
        await LookupGuard.AddOpportunityCompanyErrorAsync(
            errors, Context, entity.OpportunityId, entity.CompanyId, stored?.OpportunityId, stored?.CompanyId, cancellationToken);

        return errors;
    }

    protected override async Task BeforeDeleteAsync(Activity entity, CancellationToken cancellationToken)
    {
        // Sonraki adım zinciri kopar ama kayıtlar kalır; ilişki ve katılımcı satırları aktiviteyle birlikte gider.
        foreach (var next in await Context.Data.Set<Activity>().Where(a => a.PreviousActivityId == entity.Id).ToListAsync(cancellationToken))
        {
            next.PreviousActivityId = null;
            next.Touch();
        }

        Context.Data.Set<ActivityLink>().RemoveRange(await Context.Data.Set<ActivityLink>().Where(l => l.ActivityId == entity.Id).ToListAsync(cancellationToken));
        Context.Data.Set<ActivityParticipant>().RemoveRange(
            await Context.Data.Set<ActivityParticipant>().Where(p => p.ActivityId == entity.Id).ToListAsync(cancellationToken));
    }
}

/// <summary>Takvim penceresi süzgeci: "başlangıç|bitiş|başlangıçGünü|bitişGünü" (örnek: 2026-11-01T00:00:00Z|...|2026-11-01|2026-11-30).</summary>
internal sealed record CalendarRange(DateTimeOffset Start, DateTimeOffset End, DateOnly FromDate, DateOnly ToDate)
{
    /// <summary>Tek istekte en fazla bu kadar gün sorgulanır (sınırsız aralık sorgusu yok).</summary>
    public const int MaxDays = 93;

    public static CalendarRange? Parse(string value)
    {
        var parts = value.Split('|');
        return parts.Length == 4
            && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
            && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            && DateOnly.TryParseExact(parts[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            && end > start && end - start <= TimeSpan.FromDays(MaxDays) && to >= from
            ? new CalendarRange(start.ToUniversalTime(), end.ToUniversalTime(), from, to)
            : null;
    }

    public static System.Linq.Expressions.Expression<Func<Activity, bool>> Overlapping(CalendarRange range)
    {
        var (start, end, from, to) = (range.Start, range.End, range.FromDate, range.ToDate);
        return a =>
            (!a.IsAllDay && a.StartAt != null && a.StartAt < end
                && ((a.EndAt != null && a.EndAt > start) || (a.EndAt == null && a.StartAt >= start)))
            || (a.IsAllDay && a.StartDate != null && a.StartDate <= to && (a.EndDate ?? a.StartDate) >= from)
            || (!a.IsAllDay && a.StartAt == null && a.DueAt != null && a.DueAt >= start && a.DueAt < end);
    }
}
