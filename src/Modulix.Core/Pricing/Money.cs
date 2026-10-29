namespace Modulix.Core.Pricing;

/// <summary>Fiyatlandırma kuralı ihlali. <see cref="Code"/> makine okunur nedendir (arayüz kullanıcıya nedenini gösterir).</summary>
public sealed class PricingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PricingErrors
{
    public const string CurrencyMismatch = "currency-mismatch";
    public const string InvalidQuantity = "invalid-quantity";
    public const string InvalidUnit = "invalid-unit";
    public const string UnitGroupMismatch = "unit-group-mismatch";
    public const string InvalidFactor = "invalid-factor";
    public const string PriceNotFound = "price-not-found";
    public const string PriceConflict = "price-conflict";
    public const string TierOverlap = "tier-overlap";
    public const string NegativePrice = "negative-price";
    public const string DiscountExceedsPrice = "discount-exceeds-price";
    public const string PackageCycle = "package-cycle";
    public const string InvalidRate = "invalid-rate";
}

/// <summary>
/// Para tutarı: decimal + ISO 4217 para birimi. Farklı para birimleri toplanamaz (kur altyapısı olmadan sessiz dönüşüm yoktur).
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money operator +(Money a, Money b)
    {
        a.EnsureSameCurrency(b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        a.EnsureSameCurrency(b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new PricingException(PricingErrors.CurrencyMismatch,
                $"Farklı para birimleri doğrudan toplanamaz ({Currency} / {other.Currency}).");
        }
    }
}

/// <summary>Yuvarlama: tutarlar 2 hane, "sıfırdan uzağa" (ticari) yuvarlanır; her yerde aynı kural.</summary>
public static class MoneyRounding
{
    public const int Decimals = 2;

    public static decimal Round(decimal value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
