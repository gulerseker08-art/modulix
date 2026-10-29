namespace Modulix.Shared.Crm;

// Fiyat listeleri, vergi, kur, paket bileşeni ve indirim politikası (ADR-0018).
// Fiyat listesi: Scope Default | Customer | Segment; Status Draft | Published | Archived (yayımlanan değişmez); TierMethod Volume | Graduated.
// Birim fiyat ürünün TEMEL BİRİMİ başınadır; kademe aralığı [alt, üst) temel birim miktarıdır.

public sealed record TaxRateDto(Guid Id, long Version, string Code, string Name, decimal RatePercent, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive);

public sealed record SaveTaxRateRequest(
    string Code, string Name, decimal RatePercent, DateOnly ValidFrom, DateOnly? ValidTo = null, bool IsActive = true, long? Version = null);

public sealed record ExchangeRateDto(Guid Id, long Version, string FromCurrency, string ToCurrency, decimal Rate, DateOnly ValidOn, string Source, string? Note);

public sealed record SaveExchangeRateRequest(
    string FromCurrency, string ToCurrency, decimal Rate, DateOnly ValidOn, string Source = "Manual", string? Note = null, long? Version = null);

public sealed record PriceListDto(
    Guid Id,
    long Version,
    string Name,
    string Currency,
    string Scope,
    Guid? CompanyId,
    string? Segment,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string Status,
    string TierMethod,
    DateTimeOffset? PublishedAt);

public sealed record SavePriceListRequest(
    string Name,
    string Currency = "TRY",
    string Scope = "Default",
    Guid? CompanyId = null,
    string? Segment = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidTo = null,
    string Status = "Draft",
    string TierMethod = "Volume",
    long? Version = null);

public sealed record PriceListItemDto(
    Guid Id,
    long Version,
    Guid PriceListId,
    Guid ProductId,
    string? ProductCode,
    string? ProductName,
    decimal MinQuantity,
    decimal? MaxQuantity,
    decimal UnitPrice,
    bool IsFree);

public sealed record SavePriceListItemRequest(
    Guid PriceListId, Guid ProductId, decimal MinQuantity, decimal UnitPrice, decimal? MaxQuantity = null, bool IsFree = false, long? Version = null);

public sealed record ProductComponentDto(Guid Id, long Version, Guid PackageId, Guid ComponentId, string? ComponentCode, string? ComponentName, decimal Quantity);

public sealed record SaveProductComponentRequest(Guid PackageId, Guid ComponentId, decimal Quantity = 1m, long? Version = null);

public sealed record DiscountPolicyDto(
    Guid Id,
    long Version,
    string Name,
    decimal? Percent,
    decimal? Amount,
    string? Currency,
    int Priority,
    bool Stackable,
    decimal? MaxTotalPercent,
    decimal? ApprovalAbovePercent,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive);

public sealed record SaveDiscountPolicyRequest(
    string Name,
    decimal? Percent = null,
    decimal? Amount = null,
    string? Currency = null,
    int Priority = 0,
    bool Stackable = true,
    decimal? MaxTotalPercent = null,
    decimal? ApprovalAbovePercent = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true,
    long? Version = null);

/// <summary>Fiyat hesabı isteği (POST /api/crm/pricing/calculate). Sunucu her seferinde baştan hesaplar; istemci toplamlarına güvenmez.</summary>
public sealed record PriceCalculationRequestDto(
    Guid ProductId,
    decimal Quantity,
    string Currency,
    Guid? UnitId = null,
    DateTimeOffset? At = null,
    Guid? PriceListId = null,
    Guid? CompanyId = null,
    string? Segment = null,
    IReadOnlyList<Guid>? DiscountPolicyIds = null,
    decimal? ManualDiscountPercent = null,
    string? DisplayCurrency = null);

public sealed record PricedComponentDto(Guid ProductId, string Code, string Name, decimal BaseQuantity);

public sealed record DisplayAmountsDto(string Currency, decimal Rate, DateOnly RateDate, decimal Net, decimal Tax, decimal Gross);

public sealed record PriceCalculationDto(
    Guid ProductId,
    string ProductCode,
    string ProductName,
    decimal Quantity,
    Guid UnitId,
    decimal BaseQuantity,
    string Currency,
    Guid PriceListId,
    long PriceListVersion,
    string PriceListName,
    decimal ListTotal,
    bool IsFree,
    decimal DiscountedTotal,
    decimal EffectiveDiscountPercent,
    bool DiscountCapApplied,
    bool RequiresApproval,
    decimal TaxRatePercent,
    bool PriceIncludesTax,
    decimal Net,
    decimal Tax,
    decimal Gross,
    IReadOnlyList<Guid> DiscountPolicyIds,
    IReadOnlyList<PricedComponentDto> Components,
    DisplayAmountsDto? Display,
    DateTimeOffset CalculatedAt,
    IReadOnlyList<string> Explanation);

/// <summary>Fiyat hesabı hatası: <c>code</c> kararlı bir anahtardır (price-not-found, price-conflict, tax-rate-not-found, …).</summary>
public sealed record PriceCalculationErrorDto(string Code, string Message);
