
namespace Modulix.Modules.Crm.Pricing;

/// <param name="UnitId">Satış birimi; boşsa ürünün temel birimi.</param>
/// <param name="Currency">Fiyatın para birimi (fiyat listesi bu para biriminde olmalı).</param>
/// <param name="At">Hesap anı; boşsa şimdi. Geçerlilik tek bu ana göre denetlenir.</param>
/// <param name="PriceListId">Açık seçilen liste; geçersizse başka listeye düşülmez.</param>
/// <param name="CompanyId">Müşteriye özel liste için firma.</param>
/// <param name="Segment">Segment/kanal listesi için ad.</param>
/// <param name="DiscountPolicyIds">Uygulanacak indirim politikaları.</param>
/// <param name="ManualDiscountPercent">Elle girilen ek indirim yüzdesi.</param>
/// <param name="DisplayCurrency">Doğrudan kur kaydıyla gösterim para birimine çevrilir.</param>
public sealed record PriceCalculationRequest(
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

public sealed record PricedComponent(Guid ProductId, string Code, string Name, decimal BaseQuantity);

public sealed record DisplayAmounts(string Currency, decimal Rate, DateOnly RateDate, decimal Net, decimal Tax, decimal Gross);

/// <summary>
/// Satır fiyat hesabı: sunucu tek kaynaktır, istemci toplamlarına güvenilmez. Maliyet hiçbir zaman dönmez. Teklif satırı bu sonucu
/// anlık görüntü olarak saklar (ADR-0018 snapshot sözleşmesi).
/// </summary>
public sealed record PriceCalculationLine(
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
    IReadOnlyList<PricedComponent> Components,
    DisplayAmounts? Display,
    DateTimeOffset CalculatedAt,
    IReadOnlyList<string> Explanation);

/// <summary>Başarılıysa <see cref="Line"/>, değilse kararlı <see cref="Code"/> (PricingErrors ya da hizmet kodu) ve açıklama.</summary>
public sealed record PriceCalculationOutcome(PriceCalculationLine? Line, string? Code, string? Message)
{
    public bool IsSuccess => Line is not null;

    public static PriceCalculationOutcome Fail(string code, string message) => new(null, code, message);
}

public interface IPriceCalculator
{
    Task<PriceCalculationOutcome> CalculateAsync(PriceCalculationRequest request, CancellationToken cancellationToken);
}

public static class PriceCalculationErrors
{
    public const string ProductNotFound = "product-not-found";
    public const string NotSellable = "product-not-sellable";
    public const string TaxRateNotFound = "tax-rate-not-found";
    public const string RateNotFound = "exchange-rate-not-found";
    public const string InvalidRequest = "invalid-request";
}

