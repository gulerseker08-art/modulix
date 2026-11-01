using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Records;

/// <summary>
/// Destek kuyruğu kayıt deposu. Kuyruk adlarını talep okuyan herkes görür (seçim için); yönetim ayrı izinledir. Tenant başına tek varsayılan
/// kuyruk vardır; kullanılan kuyruk silinmez (pasife alınır).
/// </summary>
internal sealed class SupportQueueRecordStore(SupportStoreContext context) : SupportRecordStore<SupportQueue>(context)
{
    public override string EntityKey => SupportModule.Entities.Queue;

    protected override string ReadPermission => SupportModule.Permissions.TicketsRead;

    protected override string WritePermission => SupportModule.Permissions.SettingsManage;

    protected override IReadOnlyList<RecordField<SupportQueue>> Fields { get; } =
    [
        Field.Text<SupportQueue>("name", SupportQueue.NameMaxLength, q => q.Name, (q, v) => q.Name = v!, required: true),
        Field.Text<SupportQueue>("description", SupportQueue.DescriptionMaxLength, q => q.Description, (q, v) => q.Description = v),
        Field.Flag<SupportQueue>("isDefault", q => q.IsDefault, (q, v) => q.IsDefault = v),
        Field.Flag<SupportQueue>("isActive", q => q.IsActive, (q, v) => q.IsActive = v),
        Field.Id<SupportQueue>("createdBy", q => q.CreatedBy, (q, v) => q.CreatedBy = v).AsSystem(),
        Field.Id<SupportQueue>("updatedBy", q => q.UpdatedBy, (q, v) => q.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SupportQueue>> Filters { get; } = new Dictionary<string, RecordFilter<SupportQueue>>
    {
        ["isActive"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => x.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<SupportQueue> Order(IQueryable<SupportQueue> source) => source.OrderBy(q => q.Name);

    protected override IQueryable<SupportQueue> Search(IQueryable<SupportQueue> source, string term) => source.Where(q => q.Name.Contains(term));

    protected override async Task AppliedAsync(SupportQueue entity, System.Text.Json.Nodes.JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.IsDefault && !entity.IsActive)
        {
            errors.Add(new RecordFieldError("isActive", "Varsayılan kuyruk pasif olamaz; önce başka bir kuyruğu varsayılan yapın."));
        }

        if (entity.IsDefault && await Context.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.IsDefault && q.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("isDefault", "Başka bir kuyruk varsayılan; önce onun varsayılan işaretini kaldırın."));
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

    protected override async Task BeforeDeleteAsync(SupportQueue entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<SupportTicket>().AnyAsync(t => t.QueueId == entity.Id, cancellationToken)
            || await Context.Data.Set<TicketCategory>().AnyAsync(c => c.DefaultQueueId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Kuyruk talep ya da kategori tarafından kullanılıyor; silinemez. Pasife alın.");
        }
    }
}

/// <summary>
/// Talep kategorisi kayıt deposu: en çok iki düzey (kategori → alt kategori). Kullanılan ya da alt kategorisi olan kategori silinmez
/// (pasife alınır); üst kategorisi olan kategori başkasının üstü olamaz.
/// </summary>
internal sealed class TicketCategoryRecordStore(SupportStoreContext context) : SupportRecordStore<TicketCategory>(context)
{
    public override string EntityKey => SupportModule.Entities.Category;

    protected override string ReadPermission => SupportModule.Permissions.TicketsRead;

    protected override string WritePermission => SupportModule.Permissions.SettingsManage;

    protected override IReadOnlyList<RecordField<TicketCategory>> Fields { get; } =
    [
        Field.Text<TicketCategory>("name", TicketCategory.NameMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Reference<TicketCategory, TicketCategory>("parentId", "Üst kategori bulunamadı.", c => c.ParentId, (c, v) => c.ParentId = v),
        Field.Reference<TicketCategory, SupportQueue>("defaultQueueId", "Kuyruk bulunamadı.", c => c.DefaultQueueId, (c, v) => c.DefaultQueueId = v),
        Field.Flag<TicketCategory>("isActive", c => c.IsActive, (c, v) => c.IsActive = v),
        Field.Integer<TicketCategory>("position", c => c.Position, (c, v) => c.Position = v ?? 0, max: 9999),
        Field.Id<TicketCategory>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
        Field.Id<TicketCategory>("updatedBy", c => c.UpdatedBy, (c, v) => c.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TicketCategory>> Filters { get; } = new Dictionary<string, RecordFilter<TicketCategory>>
    {
        ["isActive"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => x.IsActive == flag) : null,
        ["parentId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.ParentId == id) : null,
        ["roots"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => (x.ParentId == null) == flag) : null,
    };

    protected override IOrderedQueryable<TicketCategory> Order(IQueryable<TicketCategory> source) => source.OrderBy(c => c.Position).ThenBy(c => c.Name);

    protected override IQueryable<TicketCategory> Search(IQueryable<TicketCategory> source, string term) => source.Where(c => c.Name.Contains(term));

    protected override async Task AppliedAsync(TicketCategory entity, System.Text.Json.Nodes.JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.ParentId is { } parentId)
        {
            if (parentId == entity.Id)
            {
                errors.Add(new RecordFieldError("parentId", "Kategori kendi üstü olamaz."));
            }
            else if (!await Context.Data.Set<TicketCategory>().AsNoTracking().AnyAsync(c => c.Id == parentId && c.ParentId == null, cancellationToken))
            {
                errors.Add(new RecordFieldError("parentId", "Üst kategori ana kategori olmalıdır (en çok iki düzey)."));
            }
            else if (!isNew && await Context.Data.Set<TicketCategory>().AsNoTracking().AnyAsync(c => c.ParentId == entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("parentId", "Alt kategorisi olan kategori başka bir kategorinin altına taşınamaz."));
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
    }

    protected override async Task BeforeDeleteAsync(TicketCategory entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<SupportTicket>().AnyAsync(t => t.CategoryId == entity.Id, cancellationToken)
            || await Context.Data.Set<TicketCategory>().AnyAsync(c => c.ParentId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Kategori talepte ya da alt kategoride kullanılıyor; silinemez. Pasife alın.");
        }
    }
}
