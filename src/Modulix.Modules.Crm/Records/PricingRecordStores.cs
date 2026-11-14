using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Pricing;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Vergi oranı (ADR-0018). Aynı kod için geçerlilik aralıkları çakışamaz; oran 0–100.</summary>
internal sealed class TaxRateRecordStore(CrmStoreContext context) : CrmRecordStore<TaxRate>(context)
{
    public override string EntityKey => CrmModule.Entities.TaxRate;

    protected override string ReadPermission => CrmModule.Permissions.PricesRead;

    protected override string WritePermission => CrmModule.Permissions.PricesWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<TaxRate>> Fields { get; } =
    [
        Field.Text<TaxRate>("code", TaxRate.CodeMaxLength, t => t.Code, (t, v) => t.Code = v!, required: true),
        Field.Text<TaxRate>("name", TaxRate.NameMaxLength, t => t.Name, (t, v) => t.Name = v!, required: true),
        Field.Rate<TaxRate>("ratePercent", t => t.RatePercent, (t, v) => { if (v is { } rate) { t.RatePercent = rate; } }),
        Field.Date<TaxRate>("validFrom", t => t.ValidFrom == default ? null : t.ValidFrom, (t, v) => { if (v is { } day) { t.ValidFrom = day; } }),
        Field.Date<TaxRate>("validTo", t => t.ValidTo, (t, v) => t.ValidTo = v),
        Field.Flag<TaxRate>("isActive", t => t.IsActive, (t, v) => t.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TaxRate>> Filters { get; } =
        new Dictionary<string, RecordFilter<TaxRate>>
        {
            ["code"] = (q, v, _) => q.Where(t => t.Code == v),
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(t => t.IsActive == active) : null,
        };

    protected override IOrderedQueryable<TaxRate> Order(IQueryable<TaxRate> source) => source.OrderBy(t => t.Code).ThenByDescending(t => t.ValidFrom);

    protected override IQueryable<TaxRate> Search(IQueryable<TaxRate> source, string term) =>
        source.Where(t => t.Code.Contains(term) || t.Name.Contains(term));

    protected override async Task AppliedAsync(TaxRate entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        entity.Code = entity.Code.Trim();
        if (entity.ValidFrom == default)
        {
            errors.Add(new RecordFieldError("validFrom", "Geçerlilik başlangıcı zorunludur."));
        }
        else if (entity.ValidTo is { } to && to <= entity.ValidFrom)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş, başlangıçtan sonra olmalıdır."));
        }
        else
        {
            // Aralıklar [başlangıç, bitiş) çakışamaz.
            var others = await Context.Data.Set<TaxRate>().AsNoTracking()
                .Where(t => t.Id != entity.Id && t.Code == entity.Code).Select(t => new { t.ValidFrom, t.ValidTo }).ToListAsync(cancellationToken);
            if (others.Any(o => (o.ValidTo is null || entity.ValidFrom < o.ValidTo) && (entity.ValidTo is null || o.ValidFrom < entity.ValidTo)))
            {
                errors.Add(new RecordFieldError("validFrom", "Bu vergi kodu için geçerlilik aralığı mevcut bir kayıtla çakışıyor."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(TaxRate entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<Product>().AnyAsync(p => p.TaxCategory == entity.Code, cancellationToken))
        {
            throw new RecordConflictException("Ürünlerde kullanılan vergi kodunun oranı silinemez; bitiş tarihi verin ya da pasifleştirin.");
        }
    }
}

/// <summary>Döviz kuru (ADR-0018). Aynı çift ve gün için tek kayıt; kaynağı ve notu tutulur; elle giriş de bu kayıtla yapılır.</summary>
internal sealed class ExchangeRateRecordStore(CrmStoreContext context) : CrmRecordStore<ExchangeRate>(context)
{
    public override string EntityKey => CrmModule.Entities.ExchangeRate;

    protected override string ReadPermission => CrmModule.Permissions.PricesRead;

    protected override string WritePermission => CrmModule.Permissions.PricesWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ExchangeRate>> Fields { get; } =
    [
        Field.Currency<ExchangeRate>("fromCurrency", r => r.FromCurrency, (r, v) => r.FromCurrency = v),
        Field.Currency<ExchangeRate>("toCurrency", r => r.ToCurrency, (r, v) => r.ToCurrency = v),
        Field.Decimal<ExchangeRate>("rate", r => r.Rate == 0 ? null : r.Rate, (r, v) => { if (v is { } rate) { r.Rate = rate; } }),
        Field.Date<ExchangeRate>("validOn", r => r.ValidOn == default ? null : r.ValidOn, (r, v) => { if (v is { } day) { r.ValidOn = day; } }),
        Field.Choice<ExchangeRate>("source", PricingRules.RateSources, r => r.Source, (r, v) => r.Source = v),
        Field.Text<ExchangeRate>("note", 500, r => r.Note, (r, v) => r.Note = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ExchangeRate>> Filters { get; } =
        new Dictionary<string, RecordFilter<ExchangeRate>>
        {
            ["fromCurrency"] = (q, v, _) => q.Where(r => r.FromCurrency == v.ToUpper()),
            ["toCurrency"] = (q, v, _) => q.Where(r => r.ToCurrency == v.ToUpper()),
        };

    protected override IOrderedQueryable<ExchangeRate> Order(IQueryable<ExchangeRate> source) =>
        source.OrderBy(r => r.FromCurrency).ThenBy(r => r.ToCurrency).ThenByDescending(r => r.ValidOn);

    protected override IQueryable<ExchangeRate> Search(IQueryable<ExchangeRate> source, string term) =>
        source.Where(r => r.FromCurrency.Contains(term) || r.ToCurrency.Contains(term));

    protected override async Task AppliedAsync(ExchangeRate entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.FromCurrency.Length != 3 || entity.ToCurrency.Length != 3)
        {
            errors.Add(new RecordFieldError("fromCurrency", "Kaynak ve hedef para birimi zorunludur."));
        }
        else if (entity.FromCurrency == entity.ToCurrency)
        {
            errors.Add(new RecordFieldError("toCurrency", "Kaynak ve hedef para birimi aynı olamaz."));
        }

        if (entity.Rate <= 0)
        {
            errors.Add(new RecordFieldError("rate", "Kur sıfırdan büyük olmalıdır."));
        }

        if (entity.ValidOn == default)
        {
            errors.Add(new RecordFieldError("validOn", "Kurun geçerli olduğu gün zorunludur."));
        }
        else if (await Context.Data.Set<ExchangeRate>().AnyAsync(r => r.Id != entity.Id && r.FromCurrency == entity.FromCurrency
                     && r.ToCurrency == entity.ToCurrency && r.ValidOn == entity.ValidOn, cancellationToken))
        {
            errors.Add(new RecordFieldError("validOn", "Bu para birimi çifti için o güne ait kur zaten var."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }
}

/// <summary>
/// Fiyat listesi başlığı (ADR-0018). Taslak → Yayımlandı → Arşiv. Yayımlanmış listede hiçbir alan değişmez (yalnızca arşive alınır);
/// yayımlarken en az bir satır ve geçerli (çakışmayan) kademeler gerekir. Silme yalnızca satırsız taslakta.
/// </summary>
internal sealed class PriceListRecordStore(CrmStoreContext context) : CrmRecordStore<PriceList>(context)
{
    public override string EntityKey => CrmModule.Entities.PriceList;

    protected override string ReadPermission => CrmModule.Permissions.PricesRead;

    protected override string WritePermission => CrmModule.Permissions.PricesWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<PriceList>> Fields { get; } =
    [
        Field.Text<PriceList>("name", PriceList.NameMaxLength, l => l.Name, (l, v) => l.Name = v!, required: true),
        Field.Currency<PriceList>("currency", l => l.Currency, (l, v) => l.Currency = v),
        Field.Choice<PriceList>("scope", PricingRules.Scopes, l => l.Scope, (l, v) => l.Scope = v),
        Field.Reference<PriceList, Company>("companyId", "Firma bulunamadı.", l => l.CompanyId, (l, v) => l.CompanyId = v),
        Field.Text<PriceList>("segment", 100, l => l.Segment, (l, v) => l.Segment = v),
        Field.Time<PriceList>("validFrom", l => l.ValidFrom, (l, v) => l.ValidFrom = v),
        Field.Time<PriceList>("validTo", l => l.ValidTo, (l, v) => l.ValidTo = v),
        Field.Choice<PriceList>("status", PricingRules.ListStatuses, l => l.Status, (l, v) => l.Status = v),
        Field.Choice<PriceList>("tierMethod", PricingRules.TierMethods, l => l.TierMethod, (l, v) => l.TierMethod = v),
        Field.Time<PriceList>("publishedAt", l => l.PublishedAt, (_, _) => { }),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<PriceList>> Filters { get; } =
        new Dictionary<string, RecordFilter<PriceList>>
        {
            ["status"] = (q, v, _) => PricingRules.ListStatuses.Contains(v) ? q.Where(l => l.Status == v) : null,
            ["scope"] = (q, v, _) => PricingRules.Scopes.Contains(v) ? q.Where(l => l.Scope == v) : null,
            ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.CompanyId == id) : null,
        };

    protected override IOrderedQueryable<PriceList> Order(IQueryable<PriceList> source) => source.OrderBy(l => l.Status).ThenBy(l => l.Name);

    // Liste başlıklarına göre sıralama (sort=name, sort=-validTo ...). Varsayılan sıra durum + ad; "status" anahtarı da aynı sırayı verir.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
    [
        "name", "-name", "status", "-status", "currency", "-currency", "scope", "-scope", "validFrom", "-validFrom", "validTo", "-validTo",
    ];

    protected override IOrderedQueryable<PriceList> Order(IQueryable<PriceList> source, string? sort) => sort switch
    {
        "name" => source.OrderBy(l => l.Name),
        "-name" => source.OrderByDescending(l => l.Name),
        "-status" => source.OrderByDescending(l => l.Status).ThenBy(l => l.Name),
        "currency" => source.OrderBy(l => l.Currency).ThenBy(l => l.Name),
        "-currency" => source.OrderByDescending(l => l.Currency).ThenBy(l => l.Name),
        "scope" => source.OrderBy(l => l.Scope).ThenBy(l => l.Name),
        "-scope" => source.OrderByDescending(l => l.Scope).ThenBy(l => l.Name),
        "validFrom" => source.OrderBy(l => l.ValidFrom == null).ThenBy(l => l.ValidFrom).ThenBy(l => l.Name),
        "-validFrom" => source.OrderBy(l => l.ValidFrom == null).ThenByDescending(l => l.ValidFrom).ThenBy(l => l.Name),
        "validTo" => source.OrderBy(l => l.ValidTo == null).ThenBy(l => l.ValidTo).ThenBy(l => l.Name),
        "-validTo" => source.OrderBy(l => l.ValidTo == null).ThenByDescending(l => l.ValidTo).ThenBy(l => l.Name),
        _ => Order(source),
    };

    protected override IQueryable<PriceList> Search(IQueryable<PriceList> source, string term) =>
        source.Where(l => l.Name.Contains(term) || (l.Segment != null && l.Segment.Contains(term)));

    protected override async Task AppliedAsync(PriceList entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var stored = await Context.Data.Set<PriceList>().AsNoTracking().Where(l => l.Id == entity.Id).SingleOrDefaultAsync(cancellationToken);

        if (stored is not null && !PricingRules.CanTransition(stored.Status, entity.Status))
        {
            errors.Add(new RecordFieldError("status", $"'{stored.Status}' durumundan '{entity.Status}' durumuna geçilemez."));
        }
        else if (stored is not null && stored.Status != PricingRules.ListDraft && Changed(stored, entity))
        {
            errors.Add(new RecordFieldError("status", "Yayımlanmış ya da arşivlenmiş fiyat listesi değiştirilemez; yeni liste açın."));
        }

        if (entity.Scope == PricingRules.ScopeCustomer && entity.CompanyId is null)
        {
            errors.Add(new RecordFieldError("companyId", "Müşteriye özel liste için firma seçin."));
        }

        if (entity.Scope == PricingRules.ScopeSegment && string.IsNullOrWhiteSpace(entity.Segment))
        {
            errors.Add(new RecordFieldError("segment", "Segment/kanal listesi için segment adı girin."));
        }

        if (entity.Scope != PricingRules.ScopeCustomer)
        {
            entity.CompanyId = null;
        }

        if (entity.Scope != PricingRules.ScopeSegment)
        {
            entity.Segment = null;
        }

        if (entity.ValidFrom is { } from && entity.ValidTo is { } to && from >= to)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş, başlangıçtan sonra olmalıdır."));
        }

        if (errors.Count == 0 && entity.Status == PricingRules.ListPublished && stored?.Status != PricingRules.ListPublished)
        {
            await ValidateForPublishAsync(entity, errors, cancellationToken);
            entity.PublishedAt = DateTimeOffset.UtcNow;
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private static bool Changed(PriceList stored, PriceList entity) =>
        stored.Name != entity.Name || stored.Currency != entity.Currency || stored.Scope != entity.Scope || stored.CompanyId != entity.CompanyId
        || stored.Segment != entity.Segment || stored.ValidFrom != entity.ValidFrom || stored.ValidTo != entity.ValidTo
        || stored.TierMethod != entity.TierMethod;

    private async Task ValidateForPublishAsync(PriceList entity, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        var items = await Context.Data.Set<PriceListItem>().AsNoTracking().Where(i => i.PriceListId == entity.Id).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            errors.Add(new RecordFieldError("status", "Satırı olmayan fiyat listesi yayımlanamaz."));
            return;
        }

        foreach (var byProduct in items.GroupBy(i => i.ProductId))
        {
            try
            {
                PriceSelector.ValidateTiers(PriceListItemRecordStore.EntryOf(entity, byProduct));
                if (entity.TierMethod == PricingRules.MethodGraduated)
                {
                    EnsureContiguous(byProduct.OrderBy(i => i.MinQuantity).ToList());
                }
            }
            catch (PricingException ex)
            {
                errors.Add(new RecordFieldError("status", ex.Message));
                return;
            }
        }
    }

    // Kademeli (Graduated) listede bantlar boşluksuz olmalı; aksi halde miktar karşılanamaz.
    private static void EnsureContiguous(IReadOnlyList<PriceListItem> tiers)
    {
        for (int i = 1; i < tiers.Count; i++)
        {
            if (tiers[i - 1].MaxQuantity != tiers[i].MinQuantity)
            {
                throw new PricingException(PricingErrors.TierOverlap, "Kademeli fiyatlamada miktar bantları boşluksuz olmalıdır.");
            }
        }
    }

    protected override async Task BeforeDeleteAsync(PriceList entity, CancellationToken cancellationToken)
    {
        if (entity.Status != PricingRules.ListDraft)
        {
            throw new RecordConflictException("Yayımlanmış fiyat listesi silinemez; arşive alın.");
        }

        if (await Context.Data.Set<PriceListItem>().AnyAsync(i => i.PriceListId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Satırları olan liste silinemez; önce satırları silin.");
        }
    }
}

/// <summary>Fiyat listesi satırı (kademe). Yalnızca taslak listede eklenir/değişir/silinir; ürün başına kademeler çakışamaz.</summary>
internal sealed class PriceListItemRecordStore(CrmStoreContext context) : CrmRecordStore<PriceListItem>(context)
{
    public override string EntityKey => CrmModule.Entities.PriceListItem;

    protected override string ReadPermission => CrmModule.Permissions.PricesRead;

    protected override string WritePermission => CrmModule.Permissions.PricesWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<PriceListItem>> Fields { get; } =
    [
        Field.Reference<PriceListItem, PriceList>("priceListId", "Fiyat listesi bulunamadı.", i => i.PriceListId == Guid.Empty ? null : i.PriceListId,
            (i, v) => i.PriceListId = v ?? Guid.Empty, required: true),
        Field.Reference<PriceListItem, Product>("productId", "Ürün bulunamadı.", i => i.ProductId == Guid.Empty ? null : i.ProductId,
            (i, v) => i.ProductId = v ?? Guid.Empty, required: true),
        Field.ReadOnly<PriceListItem>("productCode", i => i.Product?.Code),
        Field.ReadOnly<PriceListItem>("productName", i => i.Product?.Name),
        Field.Quantity<PriceListItem>("minQuantity", i => i.MinQuantity, (i, v) => i.MinQuantity = v ?? 0m),
        Field.Quantity<PriceListItem>("maxQuantity", i => i.MaxQuantity, (i, v) => i.MaxQuantity = v),
        Field.Quantity<PriceListItem>("unitPrice", i => i.UnitPrice, (i, v) => i.UnitPrice = v ?? 0m),
        Field.Flag<PriceListItem>("isFree", i => i.IsFree, (i, v) => i.IsFree = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<PriceListItem>> Filters { get; } =
        new Dictionary<string, RecordFilter<PriceListItem>>
        {
            ["priceListId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(i => i.PriceListId == id) : null,
            ["productId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(i => i.ProductId == id) : null,
        };

    protected override IQueryable<PriceListItem> Load(IQueryable<PriceListItem> source) => source.Include(i => i.Product);

    protected override IOrderedQueryable<PriceListItem> Order(IQueryable<PriceListItem> source) =>
        source.OrderBy(i => i.Product!.Code).ThenBy(i => i.MinQuantity);

    protected override IQueryable<PriceListItem> Search(IQueryable<PriceListItem> source, string term) =>
        source.Where(i => i.Product!.Code.Contains(term) || i.Product.Name.Contains(term));

    /// <summary>Bir ürünün bir listedeki kademelerinden fiyat seçici girişi.</summary>
    internal static PriceListEntry EntryOf(PriceList list, IEnumerable<PriceListItem> tiers) =>
        new(list.Id, (int)Math.Min(list.Version, int.MaxValue), list.Currency, ScopeOf(list), list.Status == PricingRules.ListPublished,
            list.ValidFrom, list.ValidTo, list.TierMethod == PricingRules.MethodGraduated ? TierMethod.Graduated : TierMethod.Volume,
            [.. tiers.Select(t => new PriceTier(t.MinQuantity, t.MaxQuantity, t.UnitPrice, t.IsFree))]);

    internal static PriceListScope ScopeOf(PriceList list) => list.Scope switch
    {
        PricingRules.ScopeCustomer => PriceListScope.Customer,
        PricingRules.ScopeSegment => PriceListScope.SegmentOrChannel,
        _ => PriceListScope.TenantDefault,
    };

    protected override async Task AppliedAsync(PriceListItem entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var list = await Context.Data.Set<PriceList>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == entity.PriceListId, cancellationToken);
        if (list is null)
        {
            throw new RecordValidationException([new RecordFieldError("priceListId", "Fiyat listesi bulunamadı.")]);
        }

        if (list.Status != PricingRules.ListDraft)
        {
            errors.Add(new RecordFieldError("priceListId", "Yalnızca taslak fiyat listesinin satırı değiştirilebilir."));
        }

        if (entity.IsFree)
        {
            entity.UnitPrice = 0m;
        }

        if (entity.MaxQuantity is { } max && max <= entity.MinQuantity)
        {
            errors.Add(new RecordFieldError("maxQuantity", "Üst sınır, alt sınırdan büyük olmalıdır."));
        }

        if (errors.Count == 0)
        {
            var siblings = await Context.Data.Set<PriceListItem>().AsNoTracking()
                .Where(i => i.PriceListId == entity.PriceListId && i.ProductId == entity.ProductId && i.Id != entity.Id).ToListAsync(cancellationToken);
            try
            {
                PriceSelector.ValidateTiers(EntryOf(list, [.. siblings, entity]));
            }
            catch (PricingException ex)
            {
                errors.Add(new RecordFieldError("minQuantity", ex.Message));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(PriceListItem entity, CancellationToken cancellationToken)
    {
        var status = await Context.Data.Set<PriceList>().AsNoTracking().Where(l => l.Id == entity.PriceListId).Select(l => l.Status).SingleOrDefaultAsync(cancellationToken);
        if (status != PricingRules.ListDraft)
        {
            throw new RecordConflictException("Yalnızca taslak fiyat listesinin satırı silinebilir.");
        }
    }
}

/// <summary>Paket bileşeni (ADR-0018). Bileşen kendisi olamaz; doğrudan ya da dolaylı döngü reddedilir; bir paket-bileşen çifti bir kez bulunur.</summary>
internal sealed class ProductComponentRecordStore(CrmStoreContext context) : CrmRecordStore<ProductComponent>(context)
{
    public override string EntityKey => CrmModule.Entities.ProductComponent;

    protected override string ReadPermission => CrmModule.Permissions.ProductsRead;

    protected override string WritePermission => CrmModule.Permissions.ProductsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ProductComponent>> Fields { get; } =
    [
        Field.Reference<ProductComponent, Product>("packageId", "Paket ürünü bulunamadı.", c => c.PackageId == Guid.Empty ? null : c.PackageId,
            (c, v) => c.PackageId = v ?? Guid.Empty, required: true),
        Field.Reference<ProductComponent, Product>("componentId", "Bileşen ürünü bulunamadı.", c => c.ComponentId == Guid.Empty ? null : c.ComponentId,
            (c, v) => c.ComponentId = v ?? Guid.Empty, required: true),
        Field.ReadOnly<ProductComponent>("componentCode", c => c.Component?.Code),
        Field.ReadOnly<ProductComponent>("componentName", c => c.Component?.Name),
        Field.Decimal<ProductComponent>("quantity", c => c.Quantity, (c, v) => { if (v is { } q) { c.Quantity = q; } }),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ProductComponent>> Filters { get; } =
        new Dictionary<string, RecordFilter<ProductComponent>>
        {
            ["packageId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PackageId == id) : null,
            ["componentId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.ComponentId == id) : null,
        };

    protected override IQueryable<ProductComponent> Load(IQueryable<ProductComponent> source) => source.Include(c => c.Component);

    protected override IOrderedQueryable<ProductComponent> Order(IQueryable<ProductComponent> source) => source.OrderBy(c => c.Component!.Code);

    protected override IQueryable<ProductComponent> Search(IQueryable<ProductComponent> source, string term) =>
        source.Where(c => c.Component!.Code.Contains(term) || c.Component.Name.Contains(term));

    protected override async Task AppliedAsync(ProductComponent entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.PackageId == entity.ComponentId)
        {
            errors.Add(new RecordFieldError("componentId", "Paket kendisini bileşen olarak içeremez."));
        }
        else
        {
            if (await Context.Data.Set<ProductComponent>().AnyAsync(c => c.Id != entity.Id && c.PackageId == entity.PackageId && c.ComponentId == entity.ComponentId, cancellationToken))
            {
                errors.Add(new RecordFieldError("componentId", "Bu bileşen pakette zaten var; miktarını güncelleyin."));
            }

            var edges = await Context.Data.Set<ProductComponent>().AsNoTracking().Where(c => c.Id != entity.Id)
                .Select(c => new { c.PackageId, c.ComponentId }).ToListAsync(cancellationToken);
            var graph = edges.GroupBy(e => e.PackageId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)[.. g.Select(e => e.ComponentId)]);
            graph[entity.PackageId] = [.. graph.GetValueOrDefault(entity.PackageId) ?? [], entity.ComponentId];
            try
            {
                PackageCalculator.EnsureNoCycle(entity.PackageId, graph);
            }
            catch (PricingException ex)
            {
                errors.Add(new RecordFieldError("componentId", ex.Message));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }
}

/// <summary>İndirim politikası (ADR-0018): yüzde ya da tutar (tam biri); tutar için para birimi zorunlu; geçerlilik aralığı sıralı.</summary>
internal sealed class DiscountPolicyRecordStore(CrmStoreContext context) : CrmRecordStore<DiscountPolicy>(context)
{
    public override string EntityKey => CrmModule.Entities.DiscountPolicy;

    protected override string ReadPermission => CrmModule.Permissions.PricesRead;

    protected override string WritePermission => CrmModule.Permissions.PricesWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<DiscountPolicy>> Fields { get; } =
    [
        Field.Text<DiscountPolicy>("name", DiscountPolicy.NameMaxLength, d => d.Name, (d, v) => d.Name = v!, required: true),
        Field.Rate<DiscountPolicy>("percent", d => d.Percent, (d, v) => d.Percent = v),
        Field.Money<DiscountPolicy>("amount", d => d.Amount, (d, v) => d.Amount = v),
        Field.OptionalCurrency<DiscountPolicy>("currency", d => d.Currency, (d, v) => d.Currency = v),
        Field.Count<DiscountPolicy>("priority", d => d.Priority, (d, v) => d.Priority = v),
        Field.Flag<DiscountPolicy>("stackable", d => d.Stackable, (d, v) => d.Stackable = v),
        Field.Rate<DiscountPolicy>("maxTotalPercent", d => d.MaxTotalPercent, (d, v) => d.MaxTotalPercent = v),
        Field.Rate<DiscountPolicy>("approvalAbovePercent", d => d.ApprovalAbovePercent, (d, v) => d.ApprovalAbovePercent = v),
        Field.Date<DiscountPolicy>("validFrom", d => d.ValidFrom, (d, v) => d.ValidFrom = v),
        Field.Date<DiscountPolicy>("validTo", d => d.ValidTo, (d, v) => d.ValidTo = v),
        Field.Flag<DiscountPolicy>("isActive", d => d.IsActive, (d, v) => d.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<DiscountPolicy>> Filters { get; } =
        new Dictionary<string, RecordFilter<DiscountPolicy>>
        {
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(d => d.IsActive == active) : null,
        };

    protected override IOrderedQueryable<DiscountPolicy> Order(IQueryable<DiscountPolicy> source) => source.OrderBy(d => d.Priority).ThenBy(d => d.Name);

    protected override IQueryable<DiscountPolicy> Search(IQueryable<DiscountPolicy> source, string term) => source.Where(d => d.Name.Contains(term));

    protected override Task AppliedAsync(DiscountPolicy entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if ((entity.Percent is null) == (entity.Amount is null))
        {
            errors.Add(new RecordFieldError("percent", "Yüzde ya da tutardan tam birini girin."));
        }

        if (entity.Percent is 0)
        {
            errors.Add(new RecordFieldError("percent", "Yüzde sıfırdan büyük olmalıdır."));
        }

        if (entity.Amount is 0)
        {
            errors.Add(new RecordFieldError("amount", "Tutar sıfırdan büyük olmalıdır."));
        }

        if (entity.Amount is not null && string.IsNullOrEmpty(entity.Currency))
        {
            errors.Add(new RecordFieldError("currency", "Tutar indirimi için para birimi seçin."));
        }

        if (entity.Amount is null)
        {
            entity.Currency = null;
        }

        if (entity.ValidFrom is { } from && entity.ValidTo is { } to && from > to)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş, başlangıçtan önce olamaz."));
        }

        return errors.Count > 0 ? Task.FromException(new RecordValidationException(errors)) : Task.CompletedTask;
    }
}
