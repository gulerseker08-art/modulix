using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Tests;

/// <summary>Stok hareket defteri saf kuralları (ADR-0032 K13, K18): lokasyon biçimi, bakiye etkisi, ters kayıt yönü.</summary>
public class LedgerRulesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Theory]
    [InlineData(MovementCategories.Inbound, false, true, true)]
    [InlineData(MovementCategories.Inbound, true, true, false)]
    [InlineData(MovementCategories.Inbound, false, false, false)]
    [InlineData(MovementCategories.Outbound, true, false, true)]
    [InlineData(MovementCategories.Outbound, true, true, false)]
    [InlineData(MovementCategories.Outbound, false, false, false)]
    [InlineData(MovementCategories.Transfer, true, true, true)]
    [InlineData(MovementCategories.Transfer, true, false, false)]
    [InlineData(MovementCategories.Transfer, false, true, false)]
    public void Location_shape_depends_on_the_category(string category, bool hasFrom, bool hasTo, bool valid) =>
        Assert.Equal(valid, LedgerRules.CheckShape(category, hasFrom ? A : null, hasTo ? B : null) is null);

    [Fact]
    public void Transfer_to_the_same_location_and_unknown_or_adjustment_categories_are_rejected()
    {
        Assert.NotNull(LedgerRules.CheckShape(MovementCategories.Transfer, A, A));

        // Düzeltme doğrudan kaydedilmez (özgün hareketten türer); bilinmeyen kategori de kabul edilmez.
        Assert.NotNull(LedgerRules.CheckShape(MovementCategories.Adjustment, A, B));
        Assert.NotNull(LedgerRules.CheckShape("rezervasyon", A, null));
    }

    [Fact]
    public void Receipt_adds_issue_subtracts_and_transfer_moves_without_changing_the_total()
    {
        Assert.Equal([new BalanceEffect(A, 5m)], LedgerRules.Effects(null, A, 5m));
        Assert.Equal([new BalanceEffect(A, -5m)], LedgerRules.Effects(A, null, 5m));

        var transfer = LedgerRules.Effects(A, B, 5m);
        Assert.Equal([new BalanceEffect(A, -5m), new BalanceEffect(B, 5m)], transfer);
        Assert.Equal(0m, transfer.Sum(e => e.Delta));
    }

    [Fact]
    public void Reversal_swaps_source_and_target_so_the_effect_cancels_out()
    {
        var (from, to) = LedgerRules.Reverse(A, B);
        Assert.Equal((B, A), (from, to));

        var original = LedgerRules.Effects(A, B, 7m);
        var reversal = LedgerRules.Effects(from, to, 7m);
        Assert.All(original, o => Assert.Equal(0m, o.Delta + reversal.Single(r => r.LocationId == o.LocationId).Delta));

        // Girişin tersi çıkış, çıkışın tersi giriştir.
        Assert.Equal(((Guid?)A, (Guid?)null), LedgerRules.Reverse(null, A));
        Assert.Equal(((Guid?)null, (Guid?)A), LedgerRules.Reverse(A, null));
    }
}
