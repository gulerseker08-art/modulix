namespace Modulix.Core.Pricing;

/// <summary>Birim: bir birim grubuna (adet, ağırlık, uzunluk, süre) ait; <see cref="FactorToBase"/> grubun temel birimine katsayıdır (temel birimde 1).</summary>
public sealed record UnitDefinition(string Key, string GroupKey, decimal FactorToBase);

/// <summary>Ürüne özel ambalaj dönüşümü: 1 <see cref="UnitKey"/> = <see cref="BaseQuantity"/> ürünün temel birimi (ör. 1 kutu = 12 adet).</summary>
public sealed record ProductUnitConversion(string UnitKey, decimal BaseQuantity);

/// <summary>
/// Birim dönüşümü. Aynı gruptaki birimler grup katsayılarıyla, ürüne özel birimler (kutu, paket) ürünün temel birimi üzerinden
/// çevrilir. Farklı anlam grubundaki dönüşüm (kg → adet) ürün kuralı olmadan yapılamaz. Katsayılar pozitif olmalıdır.
/// </summary>
public sealed class UnitConverter
{
    private readonly Dictionary<string, UnitDefinition> units;
    private readonly Dictionary<string, decimal> productFactors;
    private readonly UnitDefinition baseUnit;

    /// <param name="units">Tenant birim tanımları.</param>
    /// <param name="productBaseUnitKey">Ürünün temel birimi.</param>
    /// <param name="productConversions">Ürüne özel dönüşümler (anahtar, temel birim miktarı).</param>
    public UnitConverter(IEnumerable<UnitDefinition> units, string productBaseUnitKey, IEnumerable<ProductUnitConversion>? productConversions = null)
    {
        this.units = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal);
        foreach (UnitDefinition unit in units)
        {
            if (unit.FactorToBase <= 0)
            {
                throw new PricingException(PricingErrors.InvalidFactor, $"'{unit.Key}' birimi için dönüşüm katsayısı pozitif olmalıdır.");
            }

            this.units[unit.Key] = unit;
        }

        baseUnit = Find(productBaseUnitKey);
        productFactors = [];
        foreach (ProductUnitConversion conversion in productConversions ?? [])
        {
            if (conversion.BaseQuantity <= 0)
            {
                throw new PricingException(PricingErrors.InvalidFactor, $"'{conversion.UnitKey}' ürün dönüşümü pozitif olmalıdır.");
            }

            productFactors[conversion.UnitKey] = conversion.BaseQuantity;
        }
    }

    /// <summary>Miktarı ürünün temel birimine çevirir.</summary>
    public decimal ToBase(decimal quantity, string unitKey)
    {
        if (productFactors.TryGetValue(unitKey, out decimal packed))
        {
            return quantity * packed;
        }

        UnitDefinition unit = Find(unitKey);
        if (!string.Equals(unit.GroupKey, baseUnit.GroupKey, StringComparison.Ordinal))
        {
            throw new PricingException(PricingErrors.UnitGroupMismatch,
                $"'{unitKey}' ({unit.GroupKey}) ile ürünün temel birimi '{baseUnit.Key}' ({baseUnit.GroupKey}) farklı birim gruplarında; ürün dönüşümü tanımlanmalı.");
        }

        return quantity * unit.FactorToBase / baseUnit.FactorToBase;
    }

    /// <summary>Miktarı bir birimden ötekine çevirir (ikisi de ürünün temel birimiyle uyumlu olmalı).</summary>
    public decimal Convert(decimal quantity, string fromUnitKey, string toUnitKey)
    {
        decimal inBase = ToBase(quantity, fromUnitKey);
        return inBase / ToBase(1m, toUnitKey);
    }

    /// <summary>
    /// Satış birimindeki miktarın fiyat birimi cinsinden bedeli (indirim/vergi öncesi): 2 kutu × (12 adet/kutu) × 100 TL/adet = 2.400 TL.
    /// </summary>
    public decimal LineAmount(decimal quantity, string saleUnitKey, decimal unitPrice, string priceUnitKey)
    {
        decimal inPriceUnits = Convert(quantity, saleUnitKey, priceUnitKey);
        return inPriceUnits * unitPrice;
    }

    private UnitDefinition Find(string key) =>
        units.TryGetValue(key, out UnitDefinition? unit)
            ? unit
            : throw new PricingException(PricingErrors.InvalidUnit, $"'{key}' birimi tanımlı değil.");

    /// <summary>
    /// Miktar doğrulaması: pozitif ve en çok <paramref name="precision"/> ondalık (0 = tam sayı gerekir, ör. adet).
    /// </summary>
    public static void ValidateQuantity(decimal quantity, int precision)
    {
        if (quantity <= 0)
        {
            throw new PricingException(PricingErrors.InvalidQuantity, "Miktar sıfırdan büyük olmalıdır.");
        }

        if (Math.Round(quantity, precision) != quantity)
        {
            throw new PricingException(PricingErrors.InvalidQuantity,
                precision == 0 ? "Bu birim için miktar tam sayı olmalıdır." : $"Miktar en çok {precision} ondalık hane içerebilir.");
        }
    }
}
