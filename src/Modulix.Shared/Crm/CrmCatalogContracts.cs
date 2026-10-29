namespace Modulix.Shared.Crm;

// Ürün/hizmet kataloğu (ADR-0018). Maliyet (Cost, CostCurrency) yalnızca crm.product-costs.read iznine sahip kişiye gelir; izni
// olmayanda null/varsayılan döner ve yazılamaz. Durum: Draft, Active, Inactive, Withdrawn (satıştan kaldırılmış, son durum).

public sealed record ProductCategoryDto(Guid Id, long Version, string Name, Guid? ParentId, bool IsActive);

public sealed record SaveProductCategoryRequest(string Name, Guid? ParentId = null, bool IsActive = true, long? Version = null);

/// <summary>Ölçü birimi. Ürünlerde kullanılan birimin grubu ve katsayısı değiştirilemez.</summary>
public sealed record MeasureUnitDto(Guid Id, long Version, string Code, string Name, string GroupKey, decimal FactorToBase);

public sealed record SaveMeasureUnitRequest(string Code, string Name, string GroupKey, decimal FactorToBase = 1m, long? Version = null);

public sealed record ProductDto(
    Guid Id,
    long Version,
    string Code,
    string Name,
    string? Description,
    string Kind,
    string Status,
    Guid? CategoryId,
    string? CategoryName,
    Guid BaseUnitId,
    string? BaseUnitCode,
    string? ExternalCode,
    string? Barcode,
    string? Brand,
    string? Model,
    string? Tags,
    DateOnly? SaleStart,
    DateOnly? SaleEnd,
    string? TaxCategory,
    bool PriceIncludesTax,
    int QuantityPrecision,
    decimal? Cost,
    string? CostCurrency,
    int? ServiceDurationMinutes,
    string? DeliveryMode,
    string ChargeType,
    string? RecurringPeriod);

public sealed record SaveProductRequest(
    string Code,
    string Name,
    Guid BaseUnitId,
    string Kind = "Product",
    string Status = "Draft",
    string? Description = null,
    Guid? CategoryId = null,
    string? ExternalCode = null,
    string? Barcode = null,
    string? Brand = null,
    string? Model = null,
    string? Tags = null,
    DateOnly? SaleStart = null,
    DateOnly? SaleEnd = null,
    string? TaxCategory = null,
    bool PriceIncludesTax = false,
    int QuantityPrecision = 0,
    decimal? Cost = null,
    string CostCurrency = "TRY",
    int? ServiceDurationMinutes = null,
    string? DeliveryMode = null,
    string ChargeType = "OneTime",
    string? RecurringPeriod = null,
    long? Version = null);
