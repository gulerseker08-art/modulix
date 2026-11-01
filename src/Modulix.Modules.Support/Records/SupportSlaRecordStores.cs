using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Records;

/// <summary>
/// İş takvimi kayıt deposu (yönetim izni). Saat dilimi, çalışma pencereleri ve tatiller sunucuda doğrulanır (geçersiz saat dilimi, çakışan pencere, bozuk tatil reddedilir).
/// Politikaya bağlı takvim silinmez (pasife alınır). Takvim değişikliği açık SLA örneklerini etkilemez: örnekler anlık görüntüyle yaşar.
/// </summary>
internal sealed class SlaCalendarRecordStore(SupportStoreContext context) : SupportRecordStore<SlaCalendar>(context)
{
    public override string EntityKey => SupportModule.Entities.SlaCalendar;

    protected override string ReadPermission => SupportModule.Permissions.SlaManage;

    protected override string WritePermission => SupportModule.Permissions.SlaManage;

    protected override IReadOnlyList<RecordField<SlaCalendar>> Fields { get; } =
    [
        Field.Text<SlaCalendar>("name", SlaCalendar.NameMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Text<SlaCalendar>("timeZoneId", SlaCalendar.ZoneMaxLength, c => c.TimeZoneId, (c, v) => c.TimeZoneId = v ?? "Europe/Istanbul"),
        Field.Flag<SlaCalendar>("isAlwaysOpen", c => c.IsAlwaysOpen, (c, v) => c.IsAlwaysOpen = v),
        Field.Text<SlaCalendar>("weeklyHours", SlaCalendar.WeeklyMaxLength, c => c.WeeklyHours, (c, v) => c.WeeklyHours = v),
        Field.Text<SlaCalendar>("holidays", SlaCalendar.HolidaysMaxLength, c => c.Holidays, (c, v) => c.Holidays = v),
        Field.Flag<SlaCalendar>("isActive", c => c.IsActive, (c, v) => c.IsActive = v),
        Field.Id<SlaCalendar>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
        Field.Id<SlaCalendar>("updatedBy", c => c.UpdatedBy, (c, v) => c.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SlaCalendar>> Filters { get; } = new Dictionary<string, RecordFilter<SlaCalendar>>
    {
        ["isActive"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => x.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<SlaCalendar> Order(IQueryable<SlaCalendar> source) => source.OrderBy(c => c.Name);

    protected override IQueryable<SlaCalendar> Search(IQueryable<SlaCalendar> source, string term) => source.Where(c => c.Name.Contains(term));

    protected override Task AppliedAsync(SlaCalendar entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var (_, errors) = CalendarSpecParser.Parse(entity.TimeZoneId, entity.IsAlwaysOpen, entity.WeeklyHours, entity.Holidays);
        if (errors.Count > 0)
        {
            throw new RecordValidationException([.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }

        return Task.CompletedTask;
    }

    protected override async Task BeforeDeleteAsync(SlaCalendar entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<SlaPolicy>().AnyAsync(p => p.CalendarId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Takvim bir SLA politikasında kullanılıyor; silinemez. Pasife alın.");
        }
    }
}

/// <summary>
/// SLA politikası kayıt deposu (yönetim izni). Her anlamlı değişiklik <c>revision</c>'ı artırır; açık SLA örnekleri anlık görüntüyle yaşar, bu yüzden politika düzenlemesi
/// onları sessizce değiştirmez. Hedeflerden en az biri zorunludur; yedek (varsayılan) politika kısıtsız olmalı ve tenant başına tek olmalıdır; eskalasyon kurallarındaki
/// kullanıcı ve kuyruk aynı tenant'ın etkin kayıtları olmalıdır. SLA örneklerinde kullanılan politika silinmez.
/// </summary>
internal sealed class SlaPolicyRecordStore(SupportStoreContext context) : SupportRecordStore<SlaPolicy>(context)
{
    public override string EntityKey => SupportModule.Entities.SlaPolicy;

    protected override string ReadPermission => SupportModule.Permissions.SlaManage;

    protected override string WritePermission => SupportModule.Permissions.SlaManage;

    protected override IReadOnlyList<RecordField<SlaPolicy>> Fields { get; } =
    [
        Field.Text<SlaPolicy>("name", SlaPolicy.NameMaxLength, p => p.Name, (p, v) => p.Name = v!, required: true),
        Field.Text<SlaPolicy>("description", SlaPolicy.DescriptionMaxLength, p => p.Description, (p, v) => p.Description = v),
        Field.Flag<SlaPolicy>("isActive", p => p.IsActive, (p, v) => p.IsActive = v),
        Field.Flag<SlaPolicy>("isDefault", p => p.IsDefault, (p, v) => p.IsDefault = v),
        Field.Integer<SlaPolicy>("precedence", p => p.Precedence, (p, v) => p.Precedence = v ?? 100, max: 1000),
        Field.ListChoice<SlaPolicy>("typeKey", SupportModule.Lists.TicketType, p => p.TypeKey, (p, v) => p.TypeKey = v, required: false),
        Field.Reference<SlaPolicy, TicketCategory>("categoryId", "Kategori bulunamadı.", p => p.CategoryId, (p, v) => p.CategoryId = v),
        Field.OptionalChoice<SlaPolicy>("priority", TicketPriority.All, p => p.Priority, (p, v) => p.Priority = v),
        Field.Shared<SlaPolicy, Party>("partyId", "Müşteri bulunamadı.", p => p.PartyId, (p, v) => p.PartyId = v),
        Field.Integer<SlaPolicy>("firstResponseMinutes", p => p.FirstResponseMinutes, (p, v) => p.FirstResponseMinutes = v, max: SlaPolicy.MaxMinutes),
        Field.Integer<SlaPolicy>("resolutionMinutes", p => p.ResolutionMinutes, (p, v) => p.ResolutionMinutes = v, max: SlaPolicy.MaxMinutes),
        Field.Reference<SlaPolicy, SlaCalendar>("calendarId", "Takvim bulunamadı.", p => p.CalendarId, (p, v) => p.CalendarId = v),
        Field.Flag<SlaPolicy>("pauseOnWaitingCustomer", p => p.PauseOnWaitingCustomer, (p, v) => p.PauseOnWaitingCustomer = v),
        Field.Flag<SlaPolicy>("pauseOnWaitingInternal", p => p.PauseOnWaitingInternal, (p, v) => p.PauseOnWaitingInternal = v),
        Field.Text<SlaPolicy>("escalationRules", SlaPolicy.RulesMaxLength, p => p.EscalationRules, (p, v) => p.EscalationRules = v),
        Field.Integer<SlaPolicy>("revision", p => p.Revision, (p, v) => p.Revision = v ?? 1).AsSystem(),
        Field.Id<SlaPolicy>("createdBy", p => p.CreatedBy, (p, v) => p.CreatedBy = v).AsSystem(),
        Field.Id<SlaPolicy>("updatedBy", p => p.UpdatedBy, (p, v) => p.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SlaPolicy>> Filters { get; } = new Dictionary<string, RecordFilter<SlaPolicy>>
    {
        ["isActive"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => x.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<SlaPolicy> Order(IQueryable<SlaPolicy> source) => source.OrderBy(p => p.Precedence).ThenBy(p => p.Name);

    protected override IQueryable<SlaPolicy> Search(IQueryable<SlaPolicy> source, string term) => source.Where(p => p.Name.Contains(term));

    private string? fingerprint;

    private static string Fingerprint(SlaPolicy p) =>
        string.Join('|', p.Name, p.IsActive, p.IsDefault, p.Precedence, p.TypeKey, p.CategoryId, p.Priority, p.PartyId, p.FirstResponseMinutes, p.ResolutionMinutes, p.CalendarId,
            p.PauseOnWaitingCustomer, p.PauseOnWaitingInternal, p.EscalationRules);

    protected override void OnUpdating(SlaPolicy entity) => fingerprint = Fingerprint(entity);

    protected override async Task AppliedAsync(SlaPolicy entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.FirstResponseMinutes is null && entity.ResolutionMinutes is null)
        {
            errors.Add(new RecordFieldError("resolutionMinutes", "İlk yanıt ya da çözüm hedeflerinden en az biri girilmelidir."));
        }

        if (entity.FirstResponseMinutes is <= 0)
        {
            errors.Add(new RecordFieldError("firstResponseMinutes", "Hedef 1 dakikadan az olamaz."));
        }

        if (entity.ResolutionMinutes is <= 0)
        {
            errors.Add(new RecordFieldError("resolutionMinutes", "Hedef 1 dakikadan az olamaz."));
        }

        if (entity.IsDefault)
        {
            if (entity.TypeKey is not null || entity.CategoryId is not null || entity.Priority is not null || entity.PartyId is not null)
            {
                errors.Add(new RecordFieldError("isDefault", "Varsayılan (yedek) politikada eşleşme kısıtı olamaz."));
            }

            if (await Context.Data.Set<SlaPolicy>().AsNoTracking().AnyAsync(p => p.IsDefault && p.Id != entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("isDefault", "Başka bir politika varsayılan; önce onun varsayılan işaretini kaldırın."));
            }
        }

        var (rules, ruleErrors) = EscalationRuleParser.Parse(entity.EscalationRules);
        errors.AddRange(ruleErrors.Select(e => new RecordFieldError(e.Field, e.Message)));
        foreach (var rule in rules)
        {
            if (rule.UserId is { } userId && !await Context.Members.IsActiveMemberAsync(userId, cancellationToken))
            {
                errors.Add(new RecordFieldError("escalationRules", $"'{rule.Id}' kuralındaki kullanıcı bu kuruluşun üyesi değil."));
            }

            if (rule.QueueId is { } queueId && !await Context.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.Id == queueId && q.IsActive, cancellationToken))
            {
                errors.Add(new RecordFieldError("escalationRules", $"'{rule.Id}' kuralındaki kuyruk bulunamadı ya da pasif."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }
        else if (fingerprint != Fingerprint(entity))
        {
            entity.Revision++;
        }
    }

    protected override async Task BeforeDeleteAsync(SlaPolicy entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<SlaInstance>().AnyAsync(i => i.PolicyId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Politika SLA örneklerinde kullanılıyor; silinemez. Pasife alın.");
        }
    }
}
