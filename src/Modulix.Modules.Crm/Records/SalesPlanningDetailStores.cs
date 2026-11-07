using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Tahmin sürümünün fırsat satırı, <b>salt okunur</b> (ADR-0026, rapor veri kümesi için). Satırlar yalnızca tahmin iş akışıyla yazılır; bu depo hiçbir
/// kullanıcıya yazma izni vermez (yönetici dahil). Görünürlük okuma anında <b>fırsatın bugünkü sorumlusuna</b> göre belirlenir (K7): kapsamı sınırlı
/// çağıran, yalnızca bugünkü sorumlusu kapsamında olan fırsatların satırlarını görür; satırdaki <c>ownerId</c> ise anlık görüntü anındaki sorumludur.
/// Tutar ve olasılık alanları fırsat tutarı iznine bağlı hassas alandır.
/// </summary>
internal sealed class ForecastItemRecordStore(CrmStoreContext context) : CrmRecordStore<ForecastItem>(context), IRecordStore
{
    public override string EntityKey => CrmModule.Entities.ForecastItem;

    protected override string ReadPermission => CrmModule.Permissions.ForecastsRead;

    protected override string WritePermission => CrmModule.Permissions.ForecastsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) => operation == RecordOperation.Read ? null : [];

    /// <summary>Kapsam fırsatın kapsamıdır (ADR-0030): satır, fırsat türünün kapsam kuralıyla görünür.</summary>
    protected override string ScopeEntityKey => CrmModule.Entities.Opportunity;

    protected override bool UsesScope => true;

    protected override IQueryable<ForecastItem> ScopeToOwners(IQueryable<ForecastItem> source, IReadOnlySet<Guid> owners) =>
        source.Where(i => Context.Data.Set<Opportunity>().Any(o => o.Id == i.OpportunityId && o.OwnerId != null && owners.Contains(o.OwnerId.Value)));

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (record.Values["opportunityId"] is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var opportunityId))
        {
            return false;
        }

        var current = await Context.Data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == opportunityId).Select(o => o.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return current is { } owner && owners.Contains(owner);
    }

    protected override IReadOnlyList<RecordField<ForecastItem>> Fields { get; } =
    [
        Field.Id<ForecastItem>("submissionId", i => i.SubmissionId, (_, _) => { }).AsSystem(),
        Field.Id<ForecastItem>("opportunityId", i => i.OpportunityId, (_, _) => { }).AsSystem(),
        Field.Text<ForecastItem>("opportunityNumber", ForecastItem.NumberMaxLength, i => i.OpportunityNumber, (_, _) => { }).AsSystem(),
        Field.Text<ForecastItem>("title", ForecastItem.TitleMaxLength, i => i.Title, (_, _) => { }).AsSystem(),
        Field.Id<ForecastItem>("ownerId", i => i.OwnerId, (_, _) => { }).AsSystem(),
        Field.Text<ForecastItem>("stage", 40, i => i.Stage, (_, _) => { }).AsSystem(),
        Field.Money<ForecastItem>("sourceAmount", i => i.SourceAmount, (_, _) => { }).AsSystem(),
        Field.Currency<ForecastItem>("sourceCurrency", i => i.SourceCurrency, (_, _) => { }).AsSystem(),
        Field.Percent<ForecastItem>("probability", i => i.Probability, (_, _) => { }).AsSystem(),
        Field.Flag<ForecastItem>("probabilityManual", i => i.ProbabilityManual, (_, _) => { }).AsSystem(),
        Field.Date<ForecastItem>("expectedCloseDate", i => i.ExpectedCloseDate, (_, _) => { }).AsSystem(),
        Field.Rate<ForecastItem>("rateUsed", i => i.RateUsed, (_, _) => { }).AsSystem(),
        Field.Date<ForecastItem>("rateDate", i => i.RateDate, (_, _) => { }).AsSystem(),
        Field.Flag<ForecastItem>("rateMissing", i => i.RateMissing, (_, _) => { }).AsSystem(),
        Field.Money<ForecastItem>("baseAmount", i => i.BaseAmount, (_, _) => { }).AsSystem(),
        Field.Choice<ForecastItem>("baseCategory", ForecastCategory.All, i => i.BaseCategory, (_, _) => { }).AsSystem(),
        Field.Choice<ForecastItem>("category", ForecastCategory.All, i => i.Category, (_, _) => { }).AsSystem(),
        Field.Money<ForecastItem>("adjustmentAmount", i => i.AdjustmentAmount, (_, _) => { }).AsSystem(),
        Field.Percent<ForecastItem>("probabilityOverride", i => i.ProbabilityOverride, (_, _) => { }).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ForecastItem>> Filters { get; } = new Dictionary<string, RecordFilter<ForecastItem>>
    {
        ["submissionId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(i => i.SubmissionId == id) : null,
        ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(i => i.OpportunityId == id) : null,
        ["category"] = (q, v, _) => ForecastCategory.All.Contains(v) ? q.Where(i => i.Category == v) : null,
    };

    protected override IOrderedQueryable<ForecastItem> Order(IQueryable<ForecastItem> source) =>
        source.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id);

    protected override IQueryable<ForecastItem> Search(IQueryable<ForecastItem> source, string term) =>
        source.Where(i => i.Title.Contains(term) || i.OpportunityNumber.Contains(term));
}

/// <summary>
/// Satış hedefinin değer revizyonu (eski/yeni değer, gerekçe, yapan, zaman), <b>salt okunur</b> ve değişmez (ADR-0026). Revizyon yalnızca hedef değeri
/// değiştiğinde hedef deposu tarafından yazılır; bu depo yazma izni vermez. Okuma izni hedefi okuma iznidir.
/// </summary>
internal sealed class SalesTargetRevisionRecordStore(CrmStoreContext context) : CrmRecordStore<SalesTargetRevision>(context), IRecordStore
{
    public override string EntityKey => CrmModule.Entities.SalesTargetRevision;

    protected override string ReadPermission => CrmModule.Permissions.SalesTargetsRead;

    protected override string WritePermission => CrmModule.Permissions.SalesTargetsManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) => operation == RecordOperation.Read ? null : [];

    protected override IReadOnlyList<RecordField<SalesTargetRevision>> Fields { get; } =
    [
        Field.Id<SalesTargetRevision>("targetId", r => r.TargetId, (_, _) => { }).AsSystem(),
        Field.Count<SalesTargetRevision>("revisionNo", r => r.RevisionNo, (_, _) => { }).AsSystem(),
        Field.Money<SalesTargetRevision>("oldValue", r => r.OldValue, (_, _) => { }).AsSystem(),
        Field.Money<SalesTargetRevision>("newValue", r => r.NewValue, (_, _) => { }).AsSystem(),
        Field.Text<SalesTargetRevision>("reason", ForecastRules.ReasonMaxLength, r => r.Reason, (_, _) => { }).AsSystem(),
        Field.Id<SalesTargetRevision>("changedBy", r => r.ChangedBy, (_, _) => { }).AsSystem(),
        Field.Time<SalesTargetRevision>("changedAt", r => r.CreatedAt, (_, _) => { }).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SalesTargetRevision>> Filters { get; } = new Dictionary<string, RecordFilter<SalesTargetRevision>>
    {
        ["targetId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(r => r.TargetId == id) : null,
    };

    protected override IOrderedQueryable<SalesTargetRevision> Order(IQueryable<SalesTargetRevision> source) =>
        source.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RevisionNo);

    protected override IQueryable<SalesTargetRevision> Search(IQueryable<SalesTargetRevision> source, string term) =>
        source.Where(r => r.Reason.Contains(term));
}

/// <summary>
/// Tahmin düzeltme geçmişi (eski/yeni değer, gerekçe, yapan, zaman), <b>salt okunur</b> ve değişmez (ADR-0026). Satırlar yalnızca tahmin düzeltme akışıyla
/// yazılır. Görünürlük fırsatın bugünkü sorumlusuna göre belirlenir (<see cref="ForecastItemRecordStore"/> ile aynı kural). Eski/yeni değer tutar ya da
/// olasılık taşıyabildiği için fırsat tutarı iznine bağlı hassas alandır.
/// </summary>
internal sealed class ForecastAdjustmentRecordStore(CrmStoreContext context) : CrmRecordStore<ForecastAdjustment>(context), IRecordStore
{
    public override string EntityKey => CrmModule.Entities.ForecastAdjustment;

    protected override string ReadPermission => CrmModule.Permissions.ForecastsRead;

    protected override string WritePermission => CrmModule.Permissions.ForecastsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) => operation == RecordOperation.Read ? null : [];

    protected override string ScopeEntityKey => CrmModule.Entities.Opportunity;

    protected override bool UsesScope => true;

    protected override IQueryable<ForecastAdjustment> ScopeToOwners(IQueryable<ForecastAdjustment> source, IReadOnlySet<Guid> owners) =>
        source.Where(a => Context.Data.Set<Opportunity>().Any(o => o.Id == a.OpportunityId && o.OwnerId != null && owners.Contains(o.OwnerId.Value)));

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (record.Values["opportunityId"] is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var opportunityId))
        {
            return false;
        }

        var current = await Context.Data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == opportunityId).Select(o => o.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return current is { } owner && owners.Contains(owner);
    }

    protected override IReadOnlyList<RecordField<ForecastAdjustment>> Fields { get; } =
    [
        Field.Id<ForecastAdjustment>("submissionId", a => a.SubmissionId, (_, _) => { }).AsSystem(),
        Field.Id<ForecastAdjustment>("opportunityId", a => a.OpportunityId, (_, _) => { }).AsSystem(),
        Field.Choice<ForecastAdjustment>("kind", ForecastAdjustmentKind.All, a => a.Kind, (_, _) => { }).AsSystem(),
        Field.Text<ForecastAdjustment>("fromValue", ForecastAdjustment.ValueMaxLength, a => a.FromValue, (_, _) => { }).AsSystem(),
        Field.Text<ForecastAdjustment>("toValue", ForecastAdjustment.ValueMaxLength, a => a.ToValue, (_, _) => { }).AsSystem(),
        Field.Text<ForecastAdjustment>("reason", ForecastRules.ReasonMaxLength, a => a.Reason, (_, _) => { }).AsSystem(),
        Field.Id<ForecastAdjustment>("changedBy", a => a.ChangedBy, (_, _) => { }).AsSystem(),
        Field.Time<ForecastAdjustment>("changedAt", a => a.CreatedAt, (_, _) => { }).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ForecastAdjustment>> Filters { get; } = new Dictionary<string, RecordFilter<ForecastAdjustment>>
    {
        ["submissionId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.SubmissionId == id) : null,
        ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.OpportunityId == id) : null,
        ["kind"] = (q, v, _) => ForecastAdjustmentKind.All.Contains(v) ? q.Where(a => a.Kind == v) : null,
    };

    protected override IOrderedQueryable<ForecastAdjustment> Order(IQueryable<ForecastAdjustment> source) =>
        source.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id);

    protected override IQueryable<ForecastAdjustment> Search(IQueryable<ForecastAdjustment> source, string term) =>
        source.Where(a => a.Reason.Contains(term));
}
