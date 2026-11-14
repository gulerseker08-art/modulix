using Modulix.Core.Contracts.Pricing;

namespace Modulix.Core.Tests.Pricing;

/// <summary>Fiyatlandırma çekirdeği: birim dönüşümü, fiyat seçimi, kademeler, indirim, vergi/yuvarlama, paket dağıtımı.</summary>
public class PricingEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly UnitDefinition[] Units =
    [
        new("adet", "count", 1m), new("g", "weight", 1m), new("kg", "weight", 1000m), new("saat", "time", 1m),
    ];

    private static PriceListEntry List(PriceListScope scope, decimal price, Guid? id = null, bool published = true,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string currency = "TRY") =>
        new(id ?? Guid.NewGuid(), 1, currency, scope, published, from, to, TierMethod.Volume, [new PriceTier(1, null, price)]);

    // --- Birim dönüşümü ---

    [Fact]
    public void Box_of_twelve_priced_per_piece_gives_2400_for_two_boxes()
    {
        var converter = new UnitConverter(Units, "adet", [new ProductUnitConversion("kutu", 12m)]);

        Assert.Equal(2400m, converter.LineAmount(2m, "kutu", 100m, "adet"));
        Assert.Equal(24m, converter.ToBase(2m, "kutu"));
    }

    [Fact]
    public void Same_group_units_convert_and_different_groups_are_rejected()
    {
        var converter = new UnitConverter(Units, "kg");

        Assert.Equal(0.5m, converter.ToBase(500m, "g"));
        PricingException ex = Assert.Throws<PricingException>(() => converter.ToBase(1m, "adet"));
        Assert.Equal(PricingErrors.UnitGroupMismatch, ex.Code);
    }

    [Fact]
    public void Non_positive_factor_and_unknown_unit_are_rejected()
    {
        Assert.Equal(PricingErrors.InvalidFactor,
            Assert.Throws<PricingException>(() => new UnitConverter([new UnitDefinition("x", "g", 0m)], "x")).Code);
        Assert.Equal(PricingErrors.InvalidUnit,
            Assert.Throws<PricingException>(() => new UnitConverter(Units, "adet").ToBase(1m, "yok")).Code);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1.5, 0)]
    [InlineData(1.234, 2)]
    public void Invalid_quantities_are_rejected(double quantity, int precision) =>
        Assert.Equal(PricingErrors.InvalidQuantity,
            Assert.Throws<PricingException>(() => UnitConverter.ValidateQuantity((decimal)quantity, precision)).Code);

    [Fact]
    public void Valid_quantities_are_accepted() => UnitConverter.ValidateQuantity(1.25m, 2);

    // --- Fiyat seçimi ---

    [Fact]
    public void Customer_list_beats_segment_and_default()
    {
        PriceListEntry customer = List(PriceListScope.Customer, 80m);
        PriceSelection selection = PriceSelector.Select(
            [List(PriceListScope.TenantDefault, 100m), List(PriceListScope.SegmentOrChannel, 90m), customer], 2m, "TRY", Now);

        Assert.Equal(customer.ListId, selection.List.ListId);
        Assert.Equal(160m, selection.Total);
    }

    [Fact]
    public void Same_priority_conflict_is_an_error_not_a_random_pick()
    {
        PricingException ex = Assert.Throws<PricingException>(() => PriceSelector.Select(
            [List(PriceListScope.Customer, 80m), List(PriceListScope.Customer, 70m)], 1m, "TRY", Now));
        Assert.Equal(PricingErrors.PriceConflict, ex.Code);
    }

    [Fact]
    public void Missing_price_is_an_error_never_zero()
    {
        Assert.Equal(PricingErrors.PriceNotFound,
            Assert.Throws<PricingException>(() => PriceSelector.Select([], 1m, "TRY", Now)).Code);
        // Başka para birimi, yayımlanmamış, süresi dolmuş ya da henüz başlamamış liste aday değildir.
        Assert.Equal(PricingErrors.PriceNotFound, Assert.Throws<PricingException>(() => PriceSelector.Select(
            [List(PriceListScope.TenantDefault, 1m, currency: "USD"), List(PriceListScope.TenantDefault, 1m, published: false),
             List(PriceListScope.TenantDefault, 1m, to: Now), List(PriceListScope.TenantDefault, 1m, from: Now.AddSeconds(1))],
            1m, "TRY", Now)).Code);
    }

    [Fact]
    public void Validity_start_is_inclusive_and_end_is_exclusive()
    {
        Assert.Equal(5m, PriceSelector.Select([List(PriceListScope.TenantDefault, 5m, from: Now)], 1m, "TRY", Now).Total);
        Assert.Equal(PricingErrors.PriceNotFound, Assert.Throws<PricingException>(() =>
            PriceSelector.Select([List(PriceListScope.TenantDefault, 5m, to: Now)], 1m, "TRY", Now)).Code);
    }

    [Fact]
    public void Explicit_list_is_used_but_never_falls_back_when_invalid()
    {
        PriceListEntry chosen = List(PriceListScope.Explicit, 60m);
        Assert.Equal(60m, PriceSelector.Select([chosen, List(PriceListScope.Customer, 10m)], 1m, "TRY", Now, chosen.ListId).Total);

        PriceListEntry expired = List(PriceListScope.Explicit, 60m, to: Now.AddDays(-1));
        Assert.Equal(PricingErrors.PriceNotFound, Assert.Throws<PricingException>(() => PriceSelector.Select(
            [expired, List(PriceListScope.TenantDefault, 100m)], 1m, "TRY", Now, expired.ListId)).Code);
    }

    [Fact]
    public void Free_sale_is_distinct_from_missing_price()
    {
        var free = new PriceListEntry(Guid.NewGuid(), 1, "TRY", PriceListScope.TenantDefault, true, null, null, TierMethod.Volume,
            [new PriceTier(1, null, 0m, IsFree: true)]);

        PriceSelection selection = PriceSelector.Select([free], 3m, "TRY", Now);

        Assert.True(selection.IsFree);
        Assert.Equal(0m, selection.Total);
    }

    [Fact]
    public void Selection_explains_why_the_price_was_applied()
    {
        PriceSelection selection = PriceSelector.Select([List(PriceListScope.Customer, 80m)], 1m, "TRY", Now);
        Assert.Contains(selection.Explanation, line => line.Contains("müşteriye özel"));
    }

    // --- Miktar kademeleri ---

    private static PriceListEntry Tiered(TierMethod method, decimal first) => new(Guid.NewGuid(), 1, "TRY",
        PriceListScope.TenantDefault, true, null, null, method,
        [new PriceTier(first, 10m, 10m), new PriceTier(10m, null, 8m)]);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(9, 90)]
    [InlineData(10, 80)] // sınır: üst sınır hariç, 10 ikinci kademede; tüm miktar 8'den
    [InlineData(12, 96)]
    public void Volume_tiers_price_the_whole_quantity_at_one_tier(int quantity, int expected) =>
        Assert.Equal(expected, PriceSelector.Select([Tiered(TierMethod.Volume, 1m)], quantity, "TRY", Now).Total);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 100)]
    [InlineData(12, 116)] // ilk 10 × 10 + 2 × 8
    public void Graduated_tiers_price_each_band_separately(int quantity, int expected) =>
        Assert.Equal(expected, PriceSelector.Select([Tiered(TierMethod.Graduated, 0m)], quantity, "TRY", Now).Total);

    [Fact]
    public void Quantity_below_first_tier_has_no_price()
    {
        Assert.Equal(PricingErrors.PriceNotFound,
            Assert.Throws<PricingException>(() => PriceSelector.Select([Tiered(TierMethod.Volume, 5m)], 2m, "TRY", Now)).Code);
    }

    [Fact]
    public void Overlapping_tiers_and_negative_prices_are_rejected()
    {
        var overlap = new PriceListEntry(Guid.NewGuid(), 1, "TRY", PriceListScope.TenantDefault, true, null, null, TierMethod.Volume,
            [new PriceTier(1, 10m, 5m), new PriceTier(5m, null, 4m)]);
        Assert.Equal(PricingErrors.TierOverlap, Assert.Throws<PricingException>(() => PriceSelector.ValidateTiers(overlap)).Code);

        var negative = List(PriceListScope.TenantDefault, -1m);
        Assert.Equal(PricingErrors.NegativePrice, Assert.Throws<PricingException>(() => PriceSelector.ValidateTiers(negative)).Code);
    }

    // --- İndirim ---

    [Fact]
    public void Sequential_ten_and_ten_percent_is_nineteen_percent()
    {
        DiscountResult result = DiscountEngine.Apply(1000m,
        [
            new DiscountRule(Guid.NewGuid(), "A", 1, 10m, null), new DiscountRule(Guid.NewGuid(), "B", 2, 10m, null),
        ]);

        Assert.Equal(810m, result.Final);
        Assert.Equal(19m, result.EffectivePercent);
        Assert.Equal(2, result.Explanation.Count);
    }

    [Fact]
    public void Amount_discount_cannot_exceed_the_price()
    {
        Assert.Equal(PricingErrors.DiscountExceedsPrice, Assert.Throws<PricingException>(() =>
            DiscountEngine.Apply(100m, [new DiscountRule(Guid.NewGuid(), "T", 1, null, 150m)])).Code);
    }

    [Fact]
    public void Maximum_total_discount_caps_the_result_and_manual_limit_requires_approval()
    {
        DiscountResult result = DiscountEngine.Apply(1000m,
        [
            new DiscountRule(Guid.NewGuid(), "A", 1, 50m, null), new DiscountRule(Guid.NewGuid(), "B", 2, 40m, null),
        ], maxTotalPercent: 50m);

        Assert.True(result.CapApplied);
        Assert.Equal(500m, result.Final);
        Assert.True(DiscountEngine.RequiresApproval(result.EffectivePercent, userLimitPercent: 10m));
        Assert.False(DiscountEngine.RequiresApproval(5m, userLimitPercent: 10m));
    }

    [Fact]
    public void Non_stackable_discount_cannot_be_combined()
    {
        Assert.Equal(PricingErrors.PriceConflict, Assert.Throws<PricingException>(() => DiscountEngine.Apply(100m,
        [
            new DiscountRule(Guid.NewGuid(), "Kampanya", 1, 10m, null, Stackable: false), new DiscountRule(Guid.NewGuid(), "B", 2, 5m, null),
        ])).Code);
    }

    // --- Vergi ve yuvarlama ---

    [Fact]
    public void Tax_exclusive_and_inclusive_prices_give_expected_amounts()
    {
        Assert.Equal(new LineAmounts(100m, 20m, 120m), LineTotals.Calculate(100m, 20m, priceIncludesTax: false));
        Assert.Equal(new LineAmounts(100m, 20m, 120m), LineTotals.Calculate(120m, 20m, priceIncludesTax: true));
        // 10,00 KDV dahil %18: matrah 8,47, vergi 1,53; brüt korunur.
        Assert.Equal(new LineAmounts(8.47m, 1.53m, 10m), LineTotals.Calculate(10m, 18m, priceIncludesTax: true));
    }

    [Fact]
    public void Document_total_is_the_sum_of_rounded_lines()
    {
        var lines = new[] { LineTotals.Calculate(0.05m, 18m, false), LineTotals.Calculate(0.05m, 18m, false), LineTotals.Calculate(0.05m, 18m, false) };
        // Her satırda vergi 0,009 → 0,01 (sıfırdan uzağa); belge satırların toplamıdır.
        LineAmounts total = LineTotals.Sum(lines);
        Assert.Equal(0.03m, total.Tax);
        Assert.Equal(total.Gross, total.Net + total.Tax);
    }

    [Fact]
    public void Different_currencies_cannot_be_added() =>
        Assert.Equal(PricingErrors.CurrencyMismatch,
            Assert.Throws<PricingException>(() => new Money(1m, "TRY") + new Money(1m, "USD")).Code);

    // --- Paket ---

    [Fact]
    public void Fixed_package_price_does_not_add_component_prices()
    {
        Assert.Equal(90m, PackageCalculator.Total(PackagePricing.Fixed, 90m, [60m, 50m]));
        Assert.Equal(110m, PackageCalculator.Total(PackagePricing.Calculated, null, [60m, 50m]));
    }

    [Fact]
    public void Fixed_price_allocation_is_proportional_and_sums_exactly()
    {
        Assert.Equal([60m, 30m], PackageCalculator.Allocate(90m, [100m, 50m]));

        // 100 / 3 bileşen: yuvarlama farkı (0,01) ilk en büyük bileşene; her çağrıda aynı sonuç.
        IReadOnlyList<decimal> shares = PackageCalculator.Allocate(100m, [1m, 1m, 1m]);
        Assert.Equal([33.34m, 33.33m, 33.33m], shares);
        Assert.Equal(100m, shares.Sum());
        Assert.Equal(shares, PackageCalculator.Allocate(100m, [1m, 1m, 1m]));
    }

    [Fact]
    public void Package_cycles_are_rejected_direct_or_indirect()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        PackageCalculator.EnsureNoCycle(a, new Dictionary<Guid, IReadOnlyList<Guid>> { [a] = [b, c], [b] = [c] });
        Assert.Equal(PricingErrors.PackageCycle, Assert.Throws<PricingException>(() =>
            PackageCalculator.EnsureNoCycle(a, new Dictionary<Guid, IReadOnlyList<Guid>> { [a] = [b], [b] = [a] })).Code);
        Assert.Equal(PricingErrors.PackageCycle, Assert.Throws<PricingException>(() =>
            PackageCalculator.EnsureNoCycle(a, new Dictionary<Guid, IReadOnlyList<Guid>> { [a] = [a] })).Code);
    }
}
