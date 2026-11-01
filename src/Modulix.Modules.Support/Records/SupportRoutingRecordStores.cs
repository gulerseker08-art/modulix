using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Lifecycle;

namespace Modulix.Modules.Support.Records;

/// <summary>Tenant öncelik matrisi (istek boyunca önbellekli). Tanımlanmamış hücre yerleşik matristen alınır.</summary>
internal static class PriorityMatrices
{
    public static async Task<string> ComputeAsync(SupportStoreContext context, string impact, string urgency, CancellationToken cancellationToken) =>
        PriorityMatrixRules.Compute(await context.MatrixAsync(cancellationToken), impact, urgency);
}

/// <summary>
/// Öncelik matrisi hücresi kayıt deposu. Okuma talep okuma iziniyle (talep ekranı önceliği gösterir), yazma yönetim iziniyle. Hücre başına tek kayıt; matris değişikliği yalnızca
/// <b>sonradan</b> açılan ya da etki/aciliyeti düzenlenen talepleri etkiler (mevcut öncelikler korunur).
/// </summary>
internal sealed class PriorityMatrixRecordStore(SupportStoreContext context) : SupportRecordStore<PriorityMatrixEntry>(context)
{
    public override string EntityKey => SupportModule.Entities.PriorityMatrix;

    protected override string ReadPermission => SupportModule.Permissions.TicketsRead;

    protected override string WritePermission => SupportModule.Permissions.SettingsManage;

    protected override IReadOnlyList<RecordField<PriorityMatrixEntry>> Fields { get; } =
    [
        Field.Choice<PriorityMatrixEntry>("impact", TicketLevel.All, e => e.Impact, (e, v) => e.Impact = v),
        Field.Choice<PriorityMatrixEntry>("urgency", TicketLevel.All, e => e.Urgency, (e, v) => e.Urgency = v),
        Field.Choice<PriorityMatrixEntry>("priority", TicketPriority.All, e => e.Priority, (e, v) => e.Priority = v),
        Field.Id<PriorityMatrixEntry>("createdBy", e => e.CreatedBy, (e, v) => e.CreatedBy = v).AsSystem(),
        Field.Id<PriorityMatrixEntry>("updatedBy", e => e.UpdatedBy, (e, v) => e.UpdatedBy = v).AsSystem(),
    ];

    protected override IOrderedQueryable<PriorityMatrixEntry> Order(IQueryable<PriorityMatrixEntry> source) => source.OrderBy(e => e.Impact).ThenBy(e => e.Urgency);

    protected override IQueryable<PriorityMatrixEntry> Search(IQueryable<PriorityMatrixEntry> source, string term) => source.Where(e => e.Impact.Contains(term) || e.Urgency.Contains(term));

    protected override async Task AppliedAsync(PriorityMatrixEntry entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<PriorityMatrixEntry>().AsNoTracking().AnyAsync(e => e.Impact == entity.Impact && e.Urgency == entity.Urgency && e.Id != entity.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("impact", "Bu etki × aciliyet hücresi zaten tanımlı; mevcut kaydı güncelleyin.")]);
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }
    }

    protected override Task BeforeDeleteAsync(PriorityMatrixEntry entity, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Yönlendirme kuralı kayıt deposu (yönetim izni). En az bir eylem (kuyruk ya da sorumlu) zorunludur; kuyruk, kategori, müşteri ve sorumlu aynı tenant'ın etkin kayıtları olmalıdır.
/// Kural yalnızca yeni talep açılırken değerlendirilir; mevcut talepler yeniden yönlendirilmez (atama/devir eylemleri kullanılır).
/// </summary>
internal sealed class TicketRoutingRuleRecordStore(SupportStoreContext context) : SupportRecordStore<TicketRoutingRule>(context)
{
    public override string EntityKey => SupportModule.Entities.RoutingRule;

    protected override string ReadPermission => SupportModule.Permissions.SettingsManage;

    protected override string WritePermission => SupportModule.Permissions.SettingsManage;

    protected override IReadOnlyList<RecordField<TicketRoutingRule>> Fields { get; } =
    [
        Field.Text<TicketRoutingRule>("name", TicketRoutingRule.NameMaxLength, r => r.Name, (r, v) => r.Name = v!, required: true),
        Field.Integer<TicketRoutingRule>("order", r => r.Order, (r, v) => r.Order = v ?? 100, max: 1000),
        Field.Flag<TicketRoutingRule>("isActive", r => r.IsActive, (r, v) => r.IsActive = v),
        Field.OptionalChoice<TicketRoutingRule>("channel", TicketChannels.All, r => r.Channel, (r, v) => r.Channel = v),
        Field.ListChoice<TicketRoutingRule>("typeKey", SupportModule.Lists.TicketType, r => r.TypeKey, (r, v) => r.TypeKey = v, required: false),
        Field.Reference<TicketRoutingRule, TicketCategory>("categoryId", "Kategori bulunamadı.", r => r.CategoryId, (r, v) => r.CategoryId = v),
        Field.OptionalChoice<TicketRoutingRule>("priority", TicketPriority.All, r => r.Priority, (r, v) => r.Priority = v),
        Field.Shared<TicketRoutingRule, Party>("partyId", "Müşteri bulunamadı.", r => r.PartyId, (r, v) => r.PartyId = v),
        Field.Reference<TicketRoutingRule, SupportQueue>("queueId", "Kuyruk bulunamadı.", r => r.QueueId, (r, v) => r.QueueId = v),
        Field.Owner<TicketRoutingRule>("ownerId", r => r.OwnerId, (r, v) => r.OwnerId = v),
        Field.Id<TicketRoutingRule>("createdBy", r => r.CreatedBy, (r, v) => r.CreatedBy = v).AsSystem(),
        Field.Id<TicketRoutingRule>("updatedBy", r => r.UpdatedBy, (r, v) => r.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TicketRoutingRule>> Filters { get; } = new Dictionary<string, RecordFilter<TicketRoutingRule>>
    {
        ["isActive"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => x.IsActive == flag) : null,
    };

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IOrderedQueryable<TicketRoutingRule> Order(IQueryable<TicketRoutingRule> source) => source.OrderBy(r => r.Order).ThenBy(r => r.Name);

    protected override IQueryable<TicketRoutingRule> Search(IQueryable<TicketRoutingRule> source, string term) => source.Where(r => r.Name.Contains(term));

    protected override async Task AppliedAsync(TicketRoutingRule entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.QueueId is null && entity.OwnerId is null)
        {
            errors.Add(new RecordFieldError("queueId", "Kuralın en az bir eylemi olmalıdır: kuyruk ya da sorumlu."));
        }

        if (entity.QueueId is { } queueId && !await Context.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.Id == queueId && q.IsActive, cancellationToken))
        {
            errors.Add(new RecordFieldError("queueId", "Kuyruk pasif; etkin bir kuyruk seçin."));
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
    }

    protected override Task BeforeDeleteAsync(TicketRoutingRule entity, CancellationToken cancellationToken) => Task.CompletedTask;
}
