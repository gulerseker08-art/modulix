using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Customization;

public class SectorProfileRulesTests
{
    private static readonly ChoiceListDefinition Stages = new(
        "crm.stage",
        "Aşamalar",
        "crm.deal",
        "stage",
        Required: true,
        [new("open", "Açık"), new("won", "Kazanıldı")],
        [new("Lead", "Aday", "open"), new("Proposal", "Teklif", "open"), new("Won", "Kazanıldı", "won")]);

    private static readonly ModuleCatalog Modules = ModuleCatalog.Create(new CrmLike(), new Shipping());

    private static readonly SectorProfile Profile = new(
        "test",
        "Test",
        "Deneme",
        ["crm", "ship"],
        [
            new("crm.deal", "Araç tipi", CustomFieldType.Choice, Options: ["Tır", "Kamyon"]),
            new("crm.deal", "Güzergâh", CustomFieldType.Text),
            new("ship.load", "Plaka", CustomFieldType.Text),
        ],
        [new("crm.stage", "Ön görüşme", "open"), new("crm.stage", "teklif", "open")]);

    [Fact]
    public void Plan_skips_existing_labels_in_any_case_and_built_in_values()
    {
        var plan = SectorProfileRules.Plan(
            Profile,
            Modules,
            new HashSet<string> { "crm", "ship" },
            canEnableModules: false,
            new Dictionary<string, IReadOnlyList<string>> { ["crm.deal"] = ["araç tipi"] },
            new Dictionary<string, ChoiceList> { ["crm.stage"] = ChoiceListRules.Resolve(Stages, null) });

        Assert.Empty(plan.ModulesToEnable);
        Assert.Empty(plan.UnavailableModules);
        Assert.Equal(["Güzergâh", "Plaka"], plan.Fields.Select(f => f.Label));
        Assert.Equal(["Ön görüşme"], plan.ListValues.Select(v => v.Label));
    }

    [Fact]
    public void Closed_modules_are_enabled_only_when_allowed_otherwise_their_parts_are_skipped()
    {
        var lists = new Dictionary<string, ChoiceList> { ["crm.stage"] = ChoiceListRules.Resolve(Stages, null) };
        var enabled = new HashSet<string> { "crm" };

        var tenantAdmin = SectorProfileRules.Plan(Profile, Modules, enabled, canEnableModules: false, new Dictionary<string, IReadOnlyList<string>>(), lists);
        Assert.Equal(["ship"], tenantAdmin.UnavailableModules);
        Assert.Equal(["Araç tipi", "Güzergâh"], tenantAdmin.Fields.Select(f => f.Label));

        var platform = SectorProfileRules.Plan(Profile, Modules, enabled, canEnableModules: true, new Dictionary<string, IReadOnlyList<string>>(), lists);
        Assert.Equal(["ship"], platform.ModulesToEnable);
        Assert.Empty(platform.UnavailableModules);
        Assert.Equal(3, platform.Fields.Count);
    }

    [Fact]
    public void Plan_is_empty_when_everything_exists()
    {
        var plan = SectorProfileRules.Plan(
            Profile with { ListValues = [new("crm.stage", "Teklif", "open")] },
            Modules,
            new HashSet<string> { "crm", "ship" },
            canEnableModules: true,
            new Dictionary<string, IReadOnlyList<string>> { ["crm.deal"] = ["Araç tipi", "güzergâh"], ["ship.load"] = ["Plaka"] },
            new Dictionary<string, ChoiceList> { ["crm.stage"] = ChoiceListRules.Resolve(Stages, null) });

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void New_values_go_after_the_last_value_of_their_category_and_existing_values_stay_as_they_are()
    {
        var current = ChoiceListRules.Resolve(Stages, [new("Won", "Kazandık", "won", true), new("Lead", "İlk temas", "open", false), new("Proposal", "Teklif", "open", true)]);

        var inputs = SectorProfileRules.Extend(current, [new("crm.stage", "Ön görüşme", "open"), new("crm.stage", "Tedavi", "open")]);
        var values = ChoiceListRules.Apply(Stages, current, inputs);

        Assert.Equal(
            ["Won:Kazandık:True", "Lead:İlk temas:False", "Proposal:Teklif:True", "on_gorusme:Ön görüşme:True", "tedavi:Tedavi:True"],
            values.Select(v => $"{v.Key}:{v.Label}:{v.Active}"));
    }

    [Theory]
    [InlineData("missing.entity", "crm.stage", "open")]
    [InlineData("crm.deal", "crm.missing", "open")]
    [InlineData("crm.deal", "crm.stage", "lost")]
    [InlineData("crm.deal", "crm.stage", null)]
    public void Catalog_rejects_profiles_that_do_not_match_the_modules(string entityKey, string listKey, string? category)
    {
        var profile = new SectorProfile("bad", "Bozuk", "", ["crm"], [new(entityKey, "Alan", CustomFieldType.Text)], [new(listKey, "Değer", category)]);

        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog([profile], Modules));
    }

    [Fact]
    public void Catalog_rejects_unknown_modules_choice_fields_without_options_and_duplicate_keys()
    {
        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog([new("a", "A", "", ["yok"], [], [])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog([new("a", "A", "", ["core"], [], [])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog(
            [new("a", "A", "", ["crm"], [new("crm.deal", "Seçim", CustomFieldType.Choice)], [])], Modules));
        // Alan, profilin modülleri dışındaki bir modülün kayıt türüne eklenemez.
        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog(
            [new("a", "A", "", ["crm"], [new("ship.load", "Plaka", CustomFieldType.Text)], [])], Modules));
        Assert.Throws<InvalidOperationException>(() => new SectorProfileCatalog([new("a", "A", "", [], [], []), new("a", "B", "", [], [], [])], Modules));

        var catalog = new SectorProfileCatalog([Profile], Modules);
        Assert.Same(Profile, catalog.Find("test"));
        Assert.Null(catalog.Find("yok"));
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
