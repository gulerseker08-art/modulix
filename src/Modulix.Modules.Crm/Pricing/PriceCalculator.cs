using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Pricing;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

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

internal sealed class PriceCalculator(IModuleData data, TimeProvider time) : IPriceCalculator
{
    public async Task<PriceCalculationOutcome> CalculateAsync(PriceCalculationRequest request, CancellationToken cancellationToken)
    {
        var at = request.At ?? time.GetUtcNow();
        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3)
        {
            return PriceCalculationOutcome.Fail(PriceCalculationErrors.InvalidRequest, "Üç harfli para birimi kodu gerekir.");
        }

        if (request.ManualDiscountPercent is < 0 or > 100)
        {
            return PriceCalculationOutcome.Fail(PriceCalculationErrors.InvalidRequest, "Elle indirim 0 ile 100 arasında olmalıdır.");
        }

        var product = await data.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return PriceCalculationOutcome.Fail(PriceCalculationErrors.ProductNotFound, "Ürün bulunamadı.");
        }

        if (!CatalogRules.IsSellable(product.Status, product.SaleStart, product.SaleEnd, day))
        {
            return PriceCalculationOutcome.Fail(PriceCalculationErrors.NotSellable, $"'{product.Code}' bu tarihte satışa uygun değil (durum ya da satış tarihi).");
        }

        try
        {
            return await CalculateCoreAsync(request, product, currency, at, day, cancellationToken);
        }
        catch (PricingException ex)
        {
            return PriceCalculationOutcome.Fail(ex.Code, ex.Message);
        }
    }

    private async Task<PriceCalculationOutcome> CalculateCoreAsync(
        PriceCalculationRequest request, Product product, string currency, DateTimeOffset at, DateOnly day, CancellationToken cancellationToken)
    {
        var log = new List<string>();

        // 1) Birim: satış birimindeki miktar ürünün temel birimine çevrilir; fiyat listesi temel birim başınadır.
        var units = await data.Set<MeasureUnit>().AsNoTracking().ToListAsync(cancellationToken);
        var converter = new UnitConverter(units.Select(u => new UnitDefinition(u.Id.ToString(), u.GroupKey, u.FactorToBase)), product.BaseUnitId.ToString());
        var saleUnitId = request.UnitId ?? product.BaseUnitId;
        UnitConverter.ValidateQuantity(request.Quantity, product.QuantityPrecision);
        var baseQuantity = converter.ToBase(request.Quantity, saleUnitId.ToString());
        log.Add($"Miktar {request.Quantity} → temel birimde {baseQuantity}.");

        // 2) Fiyat seçimi: yalnızca yayımlanmış, para birimi uyumlu ve kapsamı eşleşen listeler.
        var segment = string.IsNullOrWhiteSpace(request.Segment) ? null : request.Segment.Trim();
        var lists = await data.Set<PriceList>().AsNoTracking()
            .Where(l => l.Status == PricingRules.ListPublished && l.Currency == currency
                && (l.Id == request.PriceListId
                    || l.Scope == PricingRules.ScopeDefault
                    || (l.Scope == PricingRules.ScopeCustomer && request.CompanyId != null && l.CompanyId == request.CompanyId)
                    || (l.Scope == PricingRules.ScopeSegment && segment != null && l.Segment == segment)))
            .ToListAsync(cancellationToken);
        var listIds = lists.Select(l => l.Id).ToList();
        var items = await data.Set<PriceListItem>().AsNoTracking()
            .Where(i => i.ProductId == product.Id && listIds.Contains(i.PriceListId)).ToListAsync(cancellationToken);
        var entries = lists
            .Select(l => PriceListItemRecordStore.EntryOf(l, items.Where(i => i.PriceListId == l.Id)) with
            {
                Scope = l.Id == request.PriceListId ? PriceListScope.Explicit : PriceListItemRecordStore.ScopeOf(l),
            })
            .Where(e => e.Tiers.Count > 0)
            .ToList();
        var selection = PriceSelector.Select(entries, baseQuantity, currency, at, request.PriceListId);
        log.AddRange(selection.Explanation);
        var chosen = lists.Single(l => l.Id == selection.List.ListId);

        // 3) İndirim: politikalar öncelik sırasıyla ardışık; elle indirim sona eklenir.
        var policies = await LoadPoliciesAsync(request.DiscountPolicyIds, currency, day, cancellationToken);
        var rules = policies.Select(p => new DiscountRule(p.Id, p.Name, p.Priority, p.Percent, p.Amount, p.Stackable)).ToList();
        if (request.ManualDiscountPercent is > 0)
        {
            rules.Add(new DiscountRule(Guid.Empty, "Elle indirim", int.MaxValue, request.ManualDiscountPercent, null));
        }

        var cap = policies.Where(p => p.MaxTotalPercent is not null).Select(p => p.MaxTotalPercent).Min();
        var discount = DiscountEngine.Apply(selection.Total, rules, cap);
        log.AddRange(discount.Explanation);

        var thresholds = policies.Where(p => p.ApprovalAbovePercent is not null).Select(p => p.ApprovalAbovePercent!.Value).ToList();
        decimal? threshold = thresholds.Count > 0 ? thresholds.Min() : request.ManualDiscountPercent is > 0 ? 0m : null;
        var approval = threshold is { } limit && DiscountEngine.RequiresApproval(discount.EffectivePercent, limit);

        // 4) Vergi: oran tarih aralığıyla tenant tablosundan; kategori verilmişse oran bulunmalı, yoksa hata.
        var (taxRate, taxLog) = await ResolveTaxAsync(product, day, cancellationToken);
        log.Add(taxLog);
        var amounts = LineTotals.Calculate(discount.Final, taxRate, product.PriceIncludesTax);

        // 5) Gösterim para birimi: yalnızca kayıtlı kurla; kur yoksa hata (tahmini kur uydurulmaz).
        var display = await ConvertAsync(request.DisplayCurrency, currency, amounts, day, cancellationToken);
        if (display is not null)
        {
            log.Add($"Gösterim: 1 {currency} = {display.Rate} {display.Currency} ({display.RateDate:yyyy-MM-dd} kuru).");
        }

        var components = await data.Set<ProductComponent>().AsNoTracking().Where(c => c.PackageId == product.Id).Include(c => c.Component)
            .Select(c => new PricedComponent(c.ComponentId, c.Component!.Code, c.Component.Name, c.Quantity * baseQuantity)).ToListAsync(cancellationToken);

        return new PriceCalculationOutcome(new PriceCalculationLine(
            product.Id, product.Code, product.Name, request.Quantity, saleUnitId, baseQuantity, currency, chosen.Id, chosen.Version, chosen.Name,
            MoneyRounding.Round(selection.Total), selection.IsFree, discount.Final, discount.EffectivePercent, discount.CapApplied, approval,
            taxRate, product.PriceIncludesTax, amounts.Net, amounts.Tax, amounts.Gross, [.. policies.Select(p => p.Id)], components, display,
            at, log), null, null);
    }

    private async Task<List<DiscountPolicy>> LoadPoliciesAsync(IReadOnlyList<Guid>? ids, string currency, DateOnly day, CancellationToken cancellationToken)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var wanted = ids.Distinct().ToList();
        var found = await data.Set<DiscountPolicy>().AsNoTracking().Where(d => wanted.Contains(d.Id)).ToListAsync(cancellationToken);
        foreach (var id in wanted)
        {
            var policy = found.SingleOrDefault(d => d.Id == id);
            if (policy is null || !policy.IsActive || (policy.ValidFrom is { } from && day < from) || (policy.ValidTo is { } to && day > to))
            {
                throw new PricingException(PriceCalculationErrors.InvalidRequest, "Seçilen indirim politikası bulunamadı ya da bu tarihte geçerli değil.");
            }

            if (policy.Amount is not null && policy.Currency != currency)
            {
                throw new PricingException(PricingErrors.CurrencyMismatch, $"'{policy.Name}' tutar indirimi {policy.Currency} cinsindendir.");
            }
        }

        return found;
    }

    private async Task<(decimal Rate, string Log)> ResolveTaxAsync(Product product, DateOnly day, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(product.TaxCategory))
        {
            return (0m, "Ürünün vergi kategorisi yok; vergi %0 uygulandı.");
        }

        var rate = await data.Set<TaxRate>().AsNoTracking()
            .Where(t => t.Code == product.TaxCategory && t.IsActive && t.ValidFrom <= day && (t.ValidTo == null || day < t.ValidTo))
            .OrderByDescending(t => t.ValidFrom).FirstOrDefaultAsync(cancellationToken)
            ?? throw new PricingException(PriceCalculationErrors.TaxRateNotFound, $"'{product.TaxCategory}' vergi kodu için {day:yyyy-MM-dd} tarihinde geçerli oran yok.");
        return (rate.RatePercent, $"Vergi %{rate.RatePercent} ({rate.Code}), {(product.PriceIncludesTax ? "fiyata dahil" : "fiyata ek")}.");
    }

    private async Task<DisplayAmounts?> ConvertAsync(string? target, string source, LineAmounts amounts, DateOnly day, CancellationToken cancellationToken)
    {
        var to = target?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(to) || to == source)
        {
            return null;
        }

        var direct = await data.Set<ExchangeRate>().AsNoTracking().Where(r => r.FromCurrency == source && r.ToCurrency == to && r.ValidOn <= day)
            .OrderByDescending(r => r.ValidOn).FirstOrDefaultAsync(cancellationToken);
        var inverse = direct is null
            ? await data.Set<ExchangeRate>().AsNoTracking().Where(r => r.FromCurrency == to && r.ToCurrency == source && r.ValidOn <= day)
                .OrderByDescending(r => r.ValidOn).FirstOrDefaultAsync(cancellationToken)
            : null;
        if (direct is null && inverse is null)
        {
            throw new PricingException(PriceCalculationErrors.RateNotFound, $"{source} → {to} için {day:yyyy-MM-dd} tarihine kadar kayıtlı kur yok.");
        }

        var rate = direct?.Rate ?? 1m / inverse!.Rate;
        var rateDate = direct?.ValidOn ?? inverse!.ValidOn;
        return new DisplayAmounts(to, rate, rateDate, MoneyRounding.Round(amounts.Net * rate), MoneyRounding.Round(amounts.Tax * rate), MoneyRounding.Round(amounts.Gross * rate));
    }
}
