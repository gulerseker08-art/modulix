using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Kaydedilmiş liste görünümü kayıt deposu (ADR-0022, dilim 9). Kullanıcıya özeldir: sorgu yalnızca isteği yapanın görünümlerini bulur, başkasının görünümü ne okunur ne
/// değiştirilir (bulunamadı). Okuma ve yazma <c>contracts.contracts.read</c> iznindedir (sözleşmeleri görebilen kişi kendi görünümünü tutar); görünüm yetki taşımaz.
/// Ad kullanıcı içinde tekildir; süzgeç metni <see cref="ContractViewRules"/> ile doğrulanır; kullanıcı başına en fazla <see cref="ContractViewRules.MaxViewsPerUser"/> görünüm.
/// </summary>
internal sealed class ContractSavedViewStore(ContractsStoreContext context) : ContractsRecordStore<ContractSavedView>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractSavedView;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.ContractsRead;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ContractSavedView>> Fields { get; } =
    [
        Field.Text<ContractSavedView>("name", ContractViewRules.NameMaxLength, v => v.Name, (v, x) => v.Name = x!, required: true),
        Field.Text<ContractSavedView>("filters", ContractViewRules.FiltersMaxLength, v => v.Filters, (v, x) => v.Filters = x ?? string.Empty),
    ];

    protected override IQueryable<ContractSavedView> Restrict(IQueryable<ContractSavedView> source)
    {
        var me = Context.CurrentUserId ?? Guid.Empty;
        return source.Where(v => v.OwnerUserId == me);
    }

    protected override IOrderedQueryable<ContractSavedView> Order(IQueryable<ContractSavedView> source) => source.OrderBy(v => v.Name);

    protected override IQueryable<ContractSavedView> Search(IQueryable<ContractSavedView> source, string term) => source.Where(v => v.Name.Contains(term));

    protected override Task<JsonObject> PrepareNewAsync(ContractSavedView entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.OwnerUserId = Context.CurrentUserId ?? Guid.Empty;
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(ContractSavedView entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        entity.Name = entity.Name.Trim();
        if (entity.Name.Length == 0)
        {
            errors.Add(new RecordFieldError("name", "Görünüm adı zorunludur."));
        }

        foreach (var error in ContractViewRules.Parse(entity.Filters).Errors)
        {
            errors.Add(new RecordFieldError("filters", error));
        }

        var mine = Context.Data.Set<ContractSavedView>().Where(v => v.OwnerUserId == entity.OwnerUserId && v.Id != entity.Id);
        if (errors.Count == 0 && await mine.AnyAsync(v => v.Name == entity.Name, cancellationToken))
        {
            errors.Add(new RecordFieldError("name", "Bu adla bir görünümünüz zaten var."));
        }

        if (isNew && errors.Count == 0 && await mine.CountAsync(cancellationToken) >= ContractViewRules.MaxViewsPerUser)
        {
            errors.Add(new RecordFieldError("name", $"En fazla {ContractViewRules.MaxViewsPerUser} görünüm kaydedebilirsiniz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }
}
