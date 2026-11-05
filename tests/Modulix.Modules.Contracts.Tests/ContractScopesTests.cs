using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>
/// "Satış sözleşmeleri" kapsamı türün adına değil firmanın tür listesindeki kategoriye bağlıdır. Kapsam yalnızca süzgeçtir; görme yetkisi vermez
/// (kayıt erişimi sunucuda her sorguda ayrıca uygulanır, bu testler yalnızca hangi türlerin kapsama girdiğini sınar).
/// </summary>
public class ContractScopesTests
{
    private static ChoiceListDefinition TypeList() =>
        ((IModule)new ContractsModule()).ChoiceLists.Single(l => l.Key == ContractsModule.Lists.ContractType);

    private static ChoiceList List(params ChoiceValue[] values) => new(ContractsModule.Lists.ContractType, values);

    [Fact]
    public void Built_in_types_are_categorized_and_every_category_has_a_type()
    {
        var definition = TypeList();
        var categories = definition.Categories.Select(c => c.Key).ToHashSet();

        Assert.All(definition.Values, v => Assert.Contains(v.Category!, categories));
        // Her kategoride en az bir etkin tür kalmalıdır (liste kaydı bunu ister); hazır değerler bunu sağlar.
        Assert.All(categories, c => Assert.Contains(definition.Values, v => v.Category == c));
        Assert.All(ContractScopes.Categories, c => Assert.Contains(c, categories));
    }

    [Fact]
    public void Sales_scope_holds_customer_facing_types_and_leaves_purchase_and_employment_out()
    {
        var resolved = Core_Resolve(TypeList());

        var sales = ContractScopes.TypeKeys(resolved, ContractsModule.TypeCategories.Sales);

        Assert.Contains("Sales", sales);
        Assert.Contains("Service", sales);
        Assert.DoesNotContain("Purchase", sales);
        Assert.DoesNotContain("Employment", sales);
        Assert.DoesNotContain("Nda", sales);
        Assert.Equal(["Purchase"], ContractScopes.TypeKeys(resolved, ContractsModule.TypeCategories.Purchase));
    }

    [Fact]
    public void Firm_defined_types_follow_the_category_they_were_given_and_inactive_types_stay_in_scope()
    {
        var list = List(
            new("Sales", "Satış", ContractsModule.TypeCategories.Sales, true, true),
            new("bayilik", "Bayilik sözleşmesi", ContractsModule.TypeCategories.Sales, true, false),
            new("eski_satis", "Eski satış", ContractsModule.TypeCategories.Sales, false, false),
            new("tedarik", "Tedarik", ContractsModule.TypeCategories.Purchase, true, false),
            new("eski_ozel", "Kategorisiz eski tür", null, true, false));

        var sales = ContractScopes.TypeKeys(list, ContractsModule.TypeCategories.Sales);

        // Pasif tür eski kayıtlarda kalabilir; kategorisiz eski tür hiçbir kapsamda sayılmaz (kategori atanınca girer).
        Assert.Equal(["Sales", "bayilik", "eski_satis"], sales);
        Assert.Equal(["tedarik"], ContractScopes.TypeKeys(list, ContractsModule.TypeCategories.Purchase));
    }

    [Theory]
    [InlineData("sales", true)]
    [InlineData("purchase", true)]
    [InlineData("employment", true)]
    [InlineData("other", true)]
    [InlineData("uydurma", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_known_categories_are_accepted(string? category, bool known) => Assert.Equal(known, ContractScopes.IsKnown(category));

    [Fact]
    public void Saved_views_accept_the_sales_preset()
    {
        var (values, errors) = ContractViewRules.Parse("preset=sales&sort=-endDate");

        Assert.Empty(errors);
        Assert.Equal("sales", values["preset"]);
        Assert.NotEmpty(ContractViewRules.Parse("preset=uydurma").Errors);
    }

    private static ChoiceList Core_Resolve(ChoiceListDefinition definition) =>
        new(definition.Key, [.. definition.Values.Select(v => new ChoiceValue(v.Key, v.Label, v.Category, true, true))]);
}
