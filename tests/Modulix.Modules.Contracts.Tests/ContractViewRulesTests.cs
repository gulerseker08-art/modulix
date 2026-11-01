using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 9: kaydedilmiş görünüm süzgeç metni ve sıralama anahtarları (saf).</summary>
public class ContractViewRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("preset=ending")]
    [InlineData("effectiveStatus=Active&sort=-endDate")]
    [InlineData("type=service&currency=TRY&search=bak%C4%B1m")]
    [InlineData("approvalStatus=Pending&signatureStatus=PartiallySigned&renewalMode=Automatic")]
    public void Valid_filters_parse_without_errors(string? filters)
    {
        Assert.Empty(ContractViewRules.Parse(filters).Errors);
    }

    [Theory]
    [InlineData("ownerId=me")]                         // süzgeç adı listede yok: görünüm kapsam/sahiplik daraltmasını taşımaz
    [InlineData("commitmentOverdue=true")]             // hızlı görünüm preset ile verilir
    [InlineData("preset=herkes")]
    [InlineData("effectiveStatus=Bilinmeyen")]
    [InlineData("approvalStatus=")]
    [InlineData("currency=try")]
    [InlineData("currency=TRYX")]
    [InlineData("sort=grossTotal")]                    // tutar sütunu sıralanamaz
    [InlineData("sort=-netTotal")]
    [InlineData("search=")]
    [InlineData("preset=ending&preset=notice")]        // aynı anahtar iki kez
    public void Unknown_keys_bad_values_and_duplicates_are_reported(string filters)
    {
        Assert.NotEmpty(ContractViewRules.Parse(filters).Errors);
    }

    [Fact]
    public void Values_are_decoded_and_the_length_limit_is_enforced()
    {
        var (values, errors) = ContractViewRules.Parse("search=bak%C4%B1m%20destek&sort=title");

        Assert.Empty(errors);
        Assert.Equal("bakım destek", values["search"]);
        Assert.Equal("title", values["sort"]);
        Assert.NotEmpty(ContractViewRules.Parse("search=" + new string('a', ContractViewRules.FiltersMaxLength)).Errors);
    }

    [Fact]
    public void Sort_keys_are_a_closed_set_with_a_descending_twin_and_no_money_columns()
    {
        Assert.True(ContractSort.IsValid("endDate"));
        Assert.True(ContractSort.IsValid("-endDate"));
        Assert.True(ContractSort.IsValid(ContractSort.Default));
        Assert.False(ContractSort.IsValid("grossTotal"));
        Assert.False(ContractSort.IsValid("title desc"));
        Assert.False(ContractSort.IsValid(null));
        Assert.Equal(ContractSort.Fields.Count * 2, ContractSort.All.Count);
        Assert.DoesNotContain(ContractSort.All, k => k.Contains("Total", StringComparison.OrdinalIgnoreCase) || k.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dashboard_counters_follow_the_list_quick_views()
    {
        // Panodaki "ending", "notice", "signature" ve "overdue" sayaçları liste ekranındaki hızlı görünüm anahtarlarıyla aynı ada sahiptir (tıklanınca aynı liste açılır).
        foreach (var key in new[] { "signature", "ending", "notice", "overdue" })
        {
            Assert.Contains(key, ContractDashboardService.CountKeys);
            Assert.Contains(key, ContractViewRules.Presets);
        }
    }
}
