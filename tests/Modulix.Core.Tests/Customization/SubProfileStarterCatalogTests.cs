using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Customization;

public class SubProfileStarterCatalogTests
{
    private static readonly ChoiceListDefinition Stages = new(
        "crm.stage", "Aşamalar", "crm.deal", "stage", Required: true, [new("open", "Açık")], [new("Lead", "Aday", "open")]);

    private static readonly ModuleCatalog Modules = ModuleCatalog.Create(new CrmLike(), new Shipping());

    private static SubProfileOptionInput Field(string target, string label, CustomFieldType type = CustomFieldType.Text, string[]? choices = null) =>
        new(SubProfileOptionKind.Field, target, label, type, Choices: choices);

    [Fact]
    public void Valid_starters_are_found_by_key_and_their_modules_come_from_the_option_targets()
    {
        var starter = new SubProfileStarter(
            "kara", "Kara", "Açıklama", "karayolu",
            [Field("crm.deal", "Güzergâh"), Field("ship.load", "Plaka"), new(SubProfileOptionKind.ListValue, "crm.stage", "Ön görüşme", Category: "open")]);

        var catalog = new SubProfileStarterCatalog([starter], Modules);

        Assert.Same(starter, catalog.Find("kara"));
        Assert.Null(catalog.Find("yok"));
        Assert.Equal(["crm", "ship"], catalog.ModulesOf(starter));
    }

    [Fact]
    public void Starter_with_a_single_module_requires_only_that_module()
    {
        var starter = new SubProfileStarter("antrepo", "Antrepo", "Açıklama", null, [Field("crm.deal", "Süre", CustomFieldType.Number)]);

        Assert.Equal(["crm"], new SubProfileStarterCatalog([starter], Modules).ModulesOf(starter));
    }

    [Theory]
    [InlineData("", "Ad")]
    [InlineData("Büyük", "Ad")]
    [InlineData("boşluk var", "Ad")]
    [InlineData("kara", " ")]
    public void Catalog_rejects_bad_keys_and_names(string key, string name)
    {
        var starter = new SubProfileStarter(key, name, "Açıklama", null, [Field("crm.deal", "Alan")]);

        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([starter], Modules));
    }

    [Fact]
    public void Catalog_rejects_starters_that_do_not_match_the_modules_or_repeat_themselves()
    {
        // Seçeneksiz, bilinmeyen kayıt türü, seçenekli olması gereken alan, aynı seçeneğin tekrarı ve tekrar eden anahtar.
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new("a", "A", "", null, [])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new("a", "A", "", null, [Field("yok.entity", "Alan")])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new("a", "A", "", null, [Field("crm.deal", "Seçim", CustomFieldType.Choice)])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new("a", "A", "", null, [Field("crm.deal", "Alan"), Field("crm.deal", "alan")])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog(
            [new("a", "A", "", null, [Field("crm.deal", "Alan")]), new("a", "B", "", null, [Field("crm.deal", "Alan")])], Modules));
    }

    [Fact]
    public void Starter_features_are_optional_but_must_be_valid_and_unique()
    {
        var options = new[] { Field("crm.deal", "Alan") };
        var withFeatures = new SubProfileStarter("a", "A", "", null, options, [new("Taşıma türü", "Komple"), new("Taşıma türü", "Parsiyel")]);
        Assert.Same(withFeatures, new SubProfileStarterCatalog([withFeatures], Modules).Find("a"));
        Assert.NotNull(new SubProfileStarterCatalog([new SubProfileStarter("b", "B", "", null, options)], Modules).Find("b"));

        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new SubProfileStarter("a", "A", "", null, options, [new("", "Komple")])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog([new SubProfileStarter("a", "A", "", null, options, [new("Kapsam", " ")])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SubProfileStarterCatalog(
            [new SubProfileStarter("a", "A", "", null, options, [new("Kapsam", "Yurt içi"), new("KAPSAM", "yurt içi")])], Modules));
    }

    private sealed class CrmLike : IModule
    {
        public string Key => "crm";

        public string DisplayName => "CRM";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("crm.deals.read", "Okur")];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities { get; } = [new("crm.deal", "Fırsat")];

        public IReadOnlyList<ChoiceListDefinition> ChoiceLists { get; } = [Stages];
    }

    private sealed class Shipping : IModule
    {
        public string Key => "ship";

        public string DisplayName => "Sevkiyat";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("ship.loads.read", "Okur")];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities { get; } = [new("ship.load", "Yük")];
    }
}
