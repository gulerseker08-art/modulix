using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Ürün kategorisi (ADR-0018). Üst kategori seçilirse alt kategoridir; döngü ve kullanılan kategorinin silinmesi reddedilir.</summary>
internal sealed class ProductCategoryRecordStore(CrmStoreContext context) : CrmRecordStore<ProductCategory>(context)
{
    private const int MaxDepth = 50;

    public override string EntityKey => CrmModule.Entities.ProductCategory;

    protected override string ReadPermission => CrmModule.Permissions.ProductsRead;

    protected override string WritePermission => CrmModule.Permissions.ProductsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ProductCategory>> Fields { get; } =
    [
        Field.Text<ProductCategory>("name", ProductCategory.NameMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Reference<ProductCategory, ProductCategory>("parentId", "Üst kategori bulunamadı.", c => c.ParentId, (c, v) => c.ParentId = v),
        Field.Flag<ProductCategory>("isActive", c => c.IsActive, (c, v) => c.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ProductCategory>> Filters { get; } =
        new Dictionary<string, RecordFilter<ProductCategory>>
        {
            ["parentId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.ParentId == id) : null,
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(c => c.IsActive == active) : null,
        };

    protected override IOrderedQueryable<ProductCategory> Order(IQueryable<ProductCategory> source) => source.OrderBy(c => c.Name);

    protected override IQueryable<ProductCategory> Search(IQueryable<ProductCategory> source, string term) =>
        source.Where(c => c.Name.Contains(term));

    protected override async Task AppliedAsync(ProductCategory entity, JsonObject values, CancellationToken cancellationToken)
    {
        // Döngü: üst zincirde kategorinin kendisi olamaz.
        Guid? current = entity.ParentId;
        for (int depth = 0; current is { } id; depth++)
        {
            if (id == entity.Id || depth > MaxDepth)
            {
                throw new RecordValidationException([new RecordFieldError("parentId", "Kategori kendi altına taşınamaz (döngü).")]);
            }

            current = await Context.Data.Set<ProductCategory>().AsNoTracking().Where(c => c.Id == id).Select(c => c.ParentId).SingleOrDefaultAsync(cancellationToken);
        }
    }

    protected override async Task BeforeDeleteAsync(ProductCategory entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<ProductCategory>().AnyAsync(c => c.ParentId == entity.Id, cancellationToken)
            || await Context.Data.Set<Product>().AnyAsync(p => p.CategoryId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Alt kategorisi ya da ürünü olan kategori silinemez; pasifleştirin.");
        }
    }
}

/// <summary>
/// Ölçü birimi. Grup ve katsayı pozitif/geçerli olmalı; ürün tarafından kullanılan birimin grubu ve katsayısı değiştirilemez
/// (kullanılmış dönüşümün değişmesi geçmiş hesapları etkilerdi), kullanılan birim silinemez.
/// </summary>
internal sealed class MeasureUnitRecordStore(CrmStoreContext context) : CrmRecordStore<MeasureUnit>(context)
{
    public override string EntityKey => CrmModule.Entities.MeasureUnit;

    protected override string ReadPermission => CrmModule.Permissions.ProductsRead;

    protected override string WritePermission => CrmModule.Permissions.ProductsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<MeasureUnit>> Fields { get; } =
    [
        Field.Text<MeasureUnit>("code", MeasureUnit.CodeMaxLength, u => u.Code, (u, v) => u.Code = v!, required: true),
        Field.Text<MeasureUnit>("name", MeasureUnit.NameMaxLength, u => u.Name, (u, v) => u.Name = v!, required: true),
        Field.Choice<MeasureUnit>("groupKey", CatalogRules.UnitGroups, u => u.GroupKey, (u, v) => u.GroupKey = v),
        Field.Decimal<MeasureUnit>("factorToBase", u => u.FactorToBase, (u, v) => { if (v is { } f) { u.FactorToBase = f; } }),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<MeasureUnit>> Filters { get; } =
        new Dictionary<string, RecordFilter<MeasureUnit>>
        {
            ["groupKey"] = (q, v, _) => CatalogRules.UnitGroups.Contains(v) ? q.Where(u => u.GroupKey == v) : null,
        };

    protected override IOrderedQueryable<MeasureUnit> Order(IQueryable<MeasureUnit> source) => source.OrderBy(u => u.GroupKey).ThenBy(u => u.Code);

    // Liste başlıklarına göre sıralama (sort=code, sort=-name ...); eşitlikte koda göre.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        ["code", "-code", "name", "-name", "groupKey", "-groupKey", "factorToBase", "-factorToBase"];

    protected override IOrderedQueryable<MeasureUnit> Order(IQueryable<MeasureUnit> source, string? sort) => sort switch
    {
        "code" => source.OrderBy(u => u.Code),
        "-code" => source.OrderByDescending(u => u.Code),
        "name" => source.OrderBy(u => u.Name).ThenBy(u => u.Code),
        "-name" => source.OrderByDescending(u => u.Name).ThenBy(u => u.Code),
        "groupKey" => source.OrderBy(u => u.GroupKey).ThenBy(u => u.Code),
        "-groupKey" => source.OrderByDescending(u => u.GroupKey).ThenBy(u => u.Code),
        "factorToBase" => source.OrderBy(u => u.FactorToBase).ThenBy(u => u.Code),
        "-factorToBase" => source.OrderByDescending(u => u.FactorToBase).ThenBy(u => u.Code),
        _ => Order(source),
    };

    protected override IQueryable<MeasureUnit> Search(IQueryable<MeasureUnit> source, string term) =>
        source.Where(u => u.Code.Contains(term) || u.Name.Contains(term));

    protected override async Task AppliedAsync(MeasureUnit entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        entity.Code = entity.Code.Trim();
        if (entity.FactorToBase <= 0)
        {
            errors.Add(new RecordFieldError("factorToBase", "Dönüşüm katsayısı sıfırdan büyük olmalıdır."));
        }

        if (await Context.Data.Set<MeasureUnit>().AnyAsync(u => u.Id != entity.Id && u.Code == entity.Code, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu birim kodu zaten kullanılıyor."));
        }

        var stored = await Context.Data.Set<MeasureUnit>().AsNoTracking().Where(u => u.Id == entity.Id)
            .Select(u => new { u.GroupKey, u.FactorToBase }).SingleOrDefaultAsync(cancellationToken);
        if (stored is not null && (stored.GroupKey != entity.GroupKey || stored.FactorToBase != entity.FactorToBase)
            && await Context.Data.Set<Product>().AnyAsync(p => p.BaseUnitId == entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("factorToBase", "Ürünlerde kullanılan birimin grubu ve katsayısı değiştirilemez; yeni birim tanımlayın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(MeasureUnit entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<Product>().AnyAsync(p => p.BaseUnitId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Ürünlerde kullanılan birim silinemez.");
        }
    }
}

/// <summary>Ürün/hizmet kartı (ADR-0018). Maliyet hassas alandır; kullanılmış (taslak dışı) ürün silinmez, pasifleştirilir.</summary>
internal sealed class ProductRecordStore(CrmStoreContext context) : CrmRecordStore<Product>(context)
{
    public override string EntityKey => CrmModule.Entities.Product;

    protected override string ReadPermission => CrmModule.Permissions.ProductsRead;

    protected override string WritePermission => CrmModule.Permissions.ProductsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<Product>> Fields { get; } =
    [
        Field.Text<Product>("code", Product.CodeMaxLength, p => p.Code, (p, v) => p.Code = v!, required: true),
        Field.Text<Product>("name", Product.NameMaxLength, p => p.Name, (p, v) => p.Name = v!, required: true),
        Field.Text<Product>("description", Product.TextMaxLength, p => p.Description, (p, v) => p.Description = v),
        Field.Choice<Product>("kind", CatalogRules.Kinds, p => p.Kind, (p, v) => p.Kind = v),
        Field.Choice<Product>("status", CatalogRules.Statuses, p => p.Status, (p, v) => p.Status = v),
        Field.Reference<Product, ProductCategory>("categoryId", "Kategori bulunamadı.", p => p.CategoryId, (p, v) => p.CategoryId = v),
        Field.ReadOnly<Product>("categoryName", p => p.Category?.Name),
        Field.Reference<Product, MeasureUnit>("baseUnitId", "Birim bulunamadı.", p => p.BaseUnitId == Guid.Empty ? null : p.BaseUnitId,
            (p, v) => p.BaseUnitId = v ?? Guid.Empty, required: true),
        Field.ReadOnly<Product>("baseUnitCode", p => p.BaseUnit?.Code),
        Field.Text<Product>("externalCode", Product.ShortMaxLength, p => p.ExternalCode, (p, v) => p.ExternalCode = v),
        Field.Text<Product>("barcode", Product.ShortMaxLength, p => p.Barcode, (p, v) => p.Barcode = v),
        Field.Text<Product>("brand", Product.ShortMaxLength, p => p.Brand, (p, v) => p.Brand = v),
        Field.Text<Product>("model", Product.ShortMaxLength, p => p.Model, (p, v) => p.Model = v),
        Field.Text<Product>("tags", 200, p => p.Tags, (p, v) => p.Tags = v),
        Field.Date<Product>("saleStart", p => p.SaleStart, (p, v) => p.SaleStart = v),
        Field.Date<Product>("saleEnd", p => p.SaleEnd, (p, v) => p.SaleEnd = v),
        Field.Text<Product>("taxCategory", 30, p => p.TaxCategory, (p, v) => p.TaxCategory = v),
        Field.Flag<Product>("priceIncludesTax", p => p.PriceIncludesTax, (p, v) => p.PriceIncludesTax = v),
        Field.Count<Product>("quantityPrecision", p => p.QuantityPrecision, (p, v) => p.QuantityPrecision = v),
        Field.Money<Product>("cost", p => p.Cost, (p, v) => p.Cost = v),
        Field.Currency<Product>("costCurrency", p => p.CostCurrency, (p, v) => p.CostCurrency = v),
        Field.Minutes<Product>("serviceDurationMinutes", 525_600, p => p.ServiceDurationMinutes, (p, v) => p.ServiceDurationMinutes = v),
        Field.OptionalChoice<Product>("deliveryMode", CatalogRules.DeliveryModes, p => p.DeliveryMode, (p, v) => p.DeliveryMode = v),
        Field.Choice<Product>("chargeType", CatalogRules.ChargeTypes, p => p.ChargeType, (p, v) => p.ChargeType = v),
        Field.OptionalChoice<Product>("recurringPeriod", CatalogRules.RecurringPeriods, p => p.RecurringPeriod, (p, v) => p.RecurringPeriod = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Product>> Filters { get; } =
        new Dictionary<string, RecordFilter<Product>>
        {
            ["status"] = (q, v, _) => CatalogRules.Statuses.Contains(v) ? q.Where(p => p.Status == v) : null,
            ["kind"] = (q, v, _) => CatalogRules.Kinds.Contains(v) ? q.Where(p => p.Kind == v) : null,
            ["categoryId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.CategoryId == id) : null,
            ["code"] = (q, v, _) => q.Where(p => p.Code == v),
            ["sellableOn"] = (q, v, _) => FilterValue.Date(v) is { } day
                ? q.Where(p => p.Status == CatalogRules.StatusActive && (p.SaleStart == null || p.SaleStart <= day) && (p.SaleEnd == null || p.SaleEnd >= day))
                : null,
        };

    protected override IQueryable<Product> Load(IQueryable<Product> source) => source.Include(p => p.Category).Include(p => p.BaseUnit);

    protected override IOrderedQueryable<Product> Order(IQueryable<Product> source) => source.OrderBy(p => p.Code);

    // Liste başlıklarına göre sıralama (sort=code, sort=-name ...). Kategori adı için kategoriye (Load ile yüklenen ilişki) göre sıralanır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
    [
        "code", "-code", "name", "-name", "kind", "-kind", "status", "-status", "category", "-category", "brand", "-brand", "tags", "-tags",
    ];

    protected override IOrderedQueryable<Product> Order(IQueryable<Product> source, string? sort) => sort switch
    {
        "-code" => source.OrderByDescending(p => p.Code),
        "name" => source.OrderBy(p => p.Name).ThenBy(p => p.Code),
        "-name" => source.OrderByDescending(p => p.Name).ThenBy(p => p.Code),
        "kind" => source.OrderBy(p => p.Kind).ThenBy(p => p.Code),
        "-kind" => source.OrderByDescending(p => p.Kind).ThenBy(p => p.Code),
        "status" => source.OrderBy(p => p.Status).ThenBy(p => p.Code),
        "-status" => source.OrderByDescending(p => p.Status).ThenBy(p => p.Code),
        "category" => source.OrderBy(p => p.Category == null).ThenBy(p => p.Category!.Name).ThenBy(p => p.Code),
        "-category" => source.OrderBy(p => p.Category == null).ThenByDescending(p => p.Category!.Name).ThenBy(p => p.Code),
        "brand" => source.OrderBy(p => p.Brand == null).ThenBy(p => p.Brand).ThenBy(p => p.Code),
        "-brand" => source.OrderBy(p => p.Brand == null).ThenByDescending(p => p.Brand).ThenBy(p => p.Code),
        "tags" => source.OrderBy(p => p.Tags == null).ThenBy(p => p.Tags).ThenBy(p => p.Code),
        "-tags" => source.OrderBy(p => p.Tags == null).ThenByDescending(p => p.Tags).ThenBy(p => p.Code),
        _ => Order(source),
    };

    protected override IQueryable<Product> Search(IQueryable<Product> source, string term) =>
        source.Where(p => p.Code.Contains(term) || p.Name.Contains(term) || (p.Barcode != null && p.Barcode.Contains(term))
            || (p.ExternalCode != null && p.ExternalCode.Contains(term)) || (p.Tags != null && p.Tags.Contains(term)));

    protected override async Task AppliedAsync(Product entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        entity.Code = entity.Code.Trim();

        if (entity.SaleStart is { } start && entity.SaleEnd is { } end && end < start)
        {
            errors.Add(new RecordFieldError("saleEnd", "Satış bitişi başlangıçtan önce olamaz."));
        }

        if (entity.QuantityPrecision is < 0 or > 4)
        {
            errors.Add(new RecordFieldError("quantityPrecision", "Miktar hassasiyeti 0 ile 4 arasında olmalıdır."));
        }

        if (entity.ChargeType == CatalogRules.ChargeRecurring && entity.RecurringPeriod is null)
        {
            errors.Add(new RecordFieldError("recurringPeriod", "Dönemsel ücret için dönem seçin."));
        }
        else if (entity.ChargeType == CatalogRules.ChargeOneTime)
        {
            entity.RecurringPeriod = null;
        }

        if (entity.Kind == CatalogRules.KindProduct && entity.ServiceDurationMinutes is not null)
        {
            errors.Add(new RecordFieldError("serviceDurationMinutes", "Hizmet süresi yalnızca hizmet kartında girilir."));
        }

        if (await Context.Data.Set<Product>().AnyAsync(p => p.Id != entity.Id && p.Code == entity.Code, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu ürün kodu bu kuruluşta zaten kullanılıyor."));
        }

        // Yaşam döngüsü: kayıttaki (veritabanındaki) durumdan yeni duruma geçiş izinli olmalı.
        string? stored = await Context.Data.Set<Product>().AsNoTracking().Where(p => p.Id == entity.Id).Select(p => p.Status).SingleOrDefaultAsync(cancellationToken);
        if (stored is not null && !CatalogRules.CanTransition(stored, entity.Status))
        {
            errors.Add(new RecordFieldError("status", $"'{stored}' durumundan '{entity.Status}' durumuna geçilemez."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(Product entity, CancellationToken cancellationToken) =>
        entity.Status == CatalogRules.StatusDraft
            ? Task.CompletedTask
            : throw new RecordConflictException("Taslak dışındaki ürün silinmez; pasifleştirin ya da satıştan kaldırın.");
}
