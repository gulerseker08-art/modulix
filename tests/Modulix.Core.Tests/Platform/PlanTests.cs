using Modulix.Core.Platform;

namespace Modulix.Core.Tests.Platform;

public class PlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_normalizes_its_key_and_keeps_modules_and_limits()
    {
        var plan = new Plan("  Baslangic-1 ", "  Başlangıç ", ["crm", "crm", "support"], new Dictionary<string, int> { ["seats"] = 5 });

        Assert.Equal("baslangic-1", plan.Key);
        Assert.Equal("Başlangıç", plan.Name);
        Assert.True(plan.IsActive);
        Assert.Equal(["crm", "support"], plan.Modules.Select(m => m.ModuleKey).Order());
        Assert.Equal(5, Assert.Single(plan.Limits).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Boşluklu Anahtar")]
    [InlineData("türkçe")]
    public void Invalid_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new Plan(key, "Ad", [], null));
    }

    [Fact]
    public void Update_changes_everything_but_the_key_and_closing_does_not_delete()
    {
        var plan = new Plan("p1", "Ad", ["crm", "support"], new Dictionary<string, int> { ["seats"] = 5 });

        plan.Update("Yeni ad", ["support"], new Dictionary<string, int>(), isActive: false);

        Assert.Equal("p1", plan.Key);
        Assert.Equal("Yeni ad", plan.Name);
        Assert.False(plan.IsActive);
        Assert.Equal("support", Assert.Single(plan.Modules).ModuleKey);
        Assert.Empty(plan.Limits); // limit yok = sınırsız
    }

    [Fact]
    public void Unknown_or_negative_limits_and_blank_names_are_rejected_without_changing_the_plan()
    {
        var plan = new Plan("p1", "Ad", ["crm"], null);

        Assert.Throws<ArgumentException>(() => plan.Update("Ad", ["crm"], new Dictionary<string, int> { ["depolama"] = 1 }, true));
        Assert.Throws<ArgumentException>(() => plan.Update("Ad", ["crm"], new Dictionary<string, int> { ["seats"] = -1 }, true));
        Assert.Throws<ArgumentException>(() => plan.Update("  ", ["crm"], null, true));
        Assert.Equal("Ad", plan.Name);
    }

    [Fact]
    public void Limit_override_needs_a_reason_a_future_end_and_a_known_non_negative_limit()
    {
        var tenant = Guid.NewGuid();
        var later = Now.AddDays(5);

        Assert.Throws<ArgumentException>(() => new LimitOverride(tenant, "seats", 5, later, " ", Now));
        Assert.Throws<ArgumentException>(() => new LimitOverride(tenant, "seats", 5, Now, "Gerekçe", Now));
        Assert.Throws<ArgumentException>(() => new LimitOverride(tenant, "seats", -1, later, "Gerekçe", Now));
        Assert.Throws<ArgumentException>(() => new LimitOverride(tenant, "depolama", 5, later, "Gerekçe", Now));
        Assert.NotNull(new LimitOverride(tenant, "seats", 5, later, "Gerekçe", Now));
    }

    [Fact]
    public void Resolution_prefers_the_active_override_then_the_plan_then_unlimited()
    {
        var tenant = Guid.NewGuid();
        var active = new LimitOverride(tenant, "seats", 10, Now.AddDays(5), "Pilot", Now);
        var revoked = new LimitOverride(tenant, "seats", 99, Now.AddDays(5), "Eski", Now.AddMinutes(-1));
        revoked.Revoke(Now);

        Assert.Equal((10, LimitSource.Override), Pair(LimitResolution.Resolve("seats", 3, [active, revoked], Now)));
        Assert.Equal((3, LimitSource.Plan), Pair(LimitResolution.Resolve("seats", 3, [revoked], Now)));
        Assert.Equal((3, LimitSource.Plan), Pair(LimitResolution.Resolve("seats", 3, [active], Now.AddDays(6)))); // istisna süresi doldu
        Assert.Equal((null, LimitSource.None), Pair(LimitResolution.Resolve("seats", null, [], Now)));
    }

    [Fact]
    public void Limit_allows_up_to_the_value_and_unlimited_always()
    {
        var limit = new ResolvedLimit("seats", 3, LimitSource.Plan, null);

        Assert.True(limit.Allows(2));
        Assert.False(limit.Allows(3));
        Assert.False(limit.Allows(7)); // aşılmış olsa da yeni kayıt reddedilir, mevcutlar etkilenmez
        Assert.True(new ResolvedLimit("seats", null, LimitSource.None, null).Allows(1000));
    }

    private static (int?, LimitSource) Pair(ResolvedLimit limit) => (limit.Value, limit.Source);
}
