using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Customization;

public class SubProfileRulesTests
{
    private static readonly ChoiceListDefinition Stages = new(
        "crm.stage",
        "Aşamalar",
        "crm.deal",
        "stage",
        Required: true,
        [new("open", "Açık"), new("won", "Kazanıldı")],
        [new("Lead", "Aday", "open"), new("Won", "Kazanıldı", "won")]);

    private static readonly ChoiceListDefinition Sources = new(
        "crm.source", "Kaynaklar", "crm.deal", "source", Required: false, [], [new("web", "Web")]);

    private static readonly ModuleCatalog Modules = ModuleCatalog.Create(new CrmLike(), new Shipping());

    [Fact]
    public void Field_option_is_trimmed_and_keeps_its_choices()
    {
        var normalized = SubProfileRules.Normalize(
            new SubProfileOptionInput(SubProfileOptionKind.Field, "crm.deal", "  Araç tipi ", CustomFieldType.Choice, Required: true, Choices: [" Tır ", "Kamyon"]), Modules);

        Assert.Equal("Araç tipi", normalized.Label);
        Assert.Equal(["Tır", "Kamyon"], normalized.Choices);
        Assert.True(normalized.Required);
        Assert.Null(normalized.Category);
    }

    [Fact]
    public void Text_field_drops_stray_category_and_list_value_drops_field_settings()
    {
        var field = SubProfileRules.Normalize(new SubProfileOptionInput(SubProfileOptionKind.Field, "crm.deal", "Güzergâh", CustomFieldType.Text, Category: "open"), Modules);
        Assert.Null(field.Category);

        var value = SubProfileRules.Normalize(
            new SubProfileOptionInput(SubProfileOptionKind.ListValue, "crm.stage", "Ön görüşme", CustomFieldType.Text, Required: true, Category: "open"), Modules);
        Assert.Null(value.FieldType);
        Assert.False(value.Required);
        Assert.Equal("open", value.Category);
    }

    [Theory]
    [InlineData(SubProfileOptionKind.Field, "yok.entity", "Alan", CustomFieldType.Text, null, null, "targetKey")]
    [InlineData(SubProfileOptionKind.Field, "crm.deal", "", CustomFieldType.Text, null, null, "label")]
    [InlineData(SubProfileOptionKind.Field, "crm.deal", "Alan", null, null, null, "fieldType")]
    [InlineData(SubProfileOptionKind.Field, "crm.deal", "Alan", CustomFieldType.Choice, null, null, "choices")]
    [InlineData(SubProfileOptionKind.Field, "crm.deal", "Alan", CustomFieldType.Text, new[] { "Bir" }, null, "choices")]
    [InlineData(SubProfileOptionKind.Field, "crm.deal", "Alan", CustomFieldType.Choice, new[] { "Bir", "bir" }, null, "choices")]
    [InlineData(SubProfileOptionKind.ListValue, "crm.yok", "Değer", null, null, "open", "targetKey")]
    [InlineData(SubProfileOptionKind.ListValue, "crm.stage", "Değer", null, null, "lost", "category")]
    [InlineData(SubProfileOptionKind.ListValue, "crm.stage", "Değer", null, null, null, "category")]
    [InlineData(SubProfileOptionKind.ListValue, "crm.source", "Değer", null, null, "open", "category")]
    public void Invalid_options_are_rejected_with_the_offending_field(
        SubProfileOptionKind kind, string target, string label, CustomFieldType? type, string[]? choices, string? category, string expectedParam)
    {
        var ex = Assert.Throws<ArgumentException>(() => SubProfileRules.Normalize(new SubProfileOptionInput(kind, target, label, type, Choices: choices, Category: category), Modules));

        Assert.Equal(expectedParam, ex.ParamName);
    }

    [Fact]
    public void Duplicate_is_same_kind_target_and_label_in_any_case()
    {
        var existing = new SubProfileOption(Guid.NewGuid(), 0) { Kind = SubProfileOptionKind.Field, TargetKey = "crm.deal", Label = "Güzergâh" };

        Assert.True(SubProfileRules.IsDuplicate(new SubProfileOptionInput(SubProfileOptionKind.Field, "crm.deal", "güzergâh"), [existing]));
        Assert.False(SubProfileRules.IsDuplicate(new SubProfileOptionInput(SubProfileOptionKind.Field, "ship.load", "Güzergâh"), [existing]));
        Assert.False(SubProfileRules.IsDuplicate(new SubProfileOptionInput(SubProfileOptionKind.ListValue, "crm.deal", "Güzergâh"), [existing]));
    }

    [Fact]
    public void Profile_contains_only_pending_options_and_derives_modules_from_their_targets()
    {
        var subProfile = new SubProfile("kara", "Kara taşımacılığı", null, null);
        var applied = Option(subProfile, 0, SubProfileOptionKind.Field, "crm.deal", "Eski");
        applied.MarkApplied();
        var pendingField = Option(subProfile, 1, SubProfileOptionKind.Field, "ship.load", "Plaka");
        pendingField.FieldType = CustomFieldType.Text;
        var pendingValue = Option(subProfile, 2, SubProfileOptionKind.ListValue, "crm.stage", "Ön görüşme");
        pendingValue.Category = "open";

        var profile = SubProfileRules.ToProfile(subProfile, [applied, pendingField, pendingValue], Modules);

        Assert.Equal("kara", profile.Key);
        Assert.Equal(["Plaka"], profile.Fields.Select(f => f.Label));
        Assert.Equal(["Ön görüşme"], profile.ListValues.Select(v => v.Label));
        Assert.Equal(["ship", "crm"], profile.Modules);
    }

    [Fact]
    public void Planning_a_sub_profile_reuses_the_add_only_rules_and_skips_closed_modules()
    {
        var subProfile = new SubProfile("kara", "Kara taşımacılığı", null, null);
        var route = Option(subProfile, 0, SubProfileOptionKind.Field, "crm.deal", "Güzergâh");
        route.FieldType = CustomFieldType.Text;
        var plate = Option(subProfile, 1, SubProfileOptionKind.Field, "ship.load", "Plaka");
        plate.FieldType = CustomFieldType.Text;
        var profile = SubProfileRules.ToProfile(subProfile, [route, plate], Modules);

        var plan = SectorProfileRules.Plan(
            profile, Modules, new HashSet<string> { "crm" }, canEnableModules: false,
            new Dictionary<string, IReadOnlyList<string>> { ["crm.deal"] = ["güzergâh"] }, new Dictionary<string, ChoiceList>());

        Assert.Empty(plan.Fields);
        Assert.Equal(["ship"], plan.UnavailableModules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Büyük")]
    [InlineData("boşluk var")]
    public void Sub_profile_rejects_codes_that_are_not_lowercase_slugs(string code)
    {
        Assert.Throws<ArgumentException>(() => new SubProfile(code, "Ad", null, null));
    }

    [Fact]
    public void Sub_profile_validates_name_and_every_change_bumps_the_revision()
    {
        Assert.Throws<ArgumentException>(() => new SubProfile("kara", " ", null, null));
        Assert.Equal("kara_yolu-2", new SubProfile("kara_yolu-2", "Kara", null, null).Code); // addan üretilen kod alt çizgi içerir
        var subProfile = new SubProfile("kara", " Kara ", " açıklama ", " kara-yolu ");
        Assert.Equal(("Kara", "açıklama", "kara-yolu"), (subProfile.Name, subProfile.Description, subProfile.ActivityCode));
        Assert.Equal(SubProfileStatus.Draft, subProfile.Status);
        var revision = subProfile.Revision;

        subProfile.Edit("Kara taşımacılığı", null, null);
        subProfile.SetStatus(SubProfileStatus.Active);

        Assert.Equal(revision + 2, subProfile.Revision);
        Assert.NotNull(subProfile.ActivatedAt);
        Assert.Throws<ArgumentException>(() => subProfile.SetStatus((SubProfileStatus)99));
    }

    [Fact]
    public void Feature_is_trimmed_and_both_parts_are_required_and_bounded()
    {
        var normalized = SubProfileRules.NormalizeFeature(new SubProfileFeatureInput("  Taşıma türü ", " Komple "));
        Assert.Equal(("Taşıma türü", "Komple"), (normalized.Group, normalized.Value));

        Assert.Equal("group", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeFeature(new SubProfileFeatureInput(" ", "Komple"))).ParamName);
        Assert.Equal("group", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeFeature(new SubProfileFeatureInput(new string('a', 61), "Komple"))).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeFeature(new SubProfileFeatureInput("Kapsam", ""))).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeFeature(new SubProfileFeatureInput("Kapsam", new string('a', 101)))).ParamName);
        // Sınırdaki uzunluklar geçerlidir.
        var atLimit = SubProfileRules.NormalizeFeature(new SubProfileFeatureInput(new string('a', 60), new string('b', 100)));
        Assert.Equal((60, 100), (atLimit.Group.Length, atLimit.Value.Length));
    }

    [Fact]
    public void Duplicate_feature_is_same_group_and_value_in_any_case_but_the_same_value_under_another_group_is_fine()
    {
        var existing = new SubProfileFeature(Guid.NewGuid(), "Taşıma türü", "Komple", 0);

        Assert.True(SubProfileRules.IsDuplicateFeature(new SubProfileFeatureInput("taşıma TÜRÜ", "komple"), [existing]));
        Assert.False(SubProfileRules.IsDuplicateFeature(new SubProfileFeatureInput("Kapsam", "Komple"), [existing]));
        Assert.False(SubProfileRules.IsDuplicateFeature(new SubProfileFeatureInput("Taşıma türü", "Parsiyel"), [existing]));
    }

    [Fact]
    public void Group_labels_collect_values_under_their_group_in_first_seen_order()
    {
        var labels = SubProfileRules.GroupLabels(
        [
            new("Taşıma türü", "Komple"), new("Kapsam", "Yurt içi"), new("taşıma türü", "Parsiyel"), new("Kapsam", "Uluslararası"),
        ]);

        Assert.Equal(["Taşıma türü: Komple, Parsiyel", "Kapsam: Yurt içi, Uluslararası"], labels);
        Assert.Empty(SubProfileRules.GroupLabels([]));
    }

    [Fact]
    public void Customization_limits_are_unlimited_by_default()
    {
        var limits = new CustomizationLimits();

        Assert.Null(limits.MaxFieldsPerEntity);
        Assert.Null(limits.MaxOptionsPerField);
        Assert.Null(limits.MaxValuesPerList);
        Assert.Null(limits.MaxSubProfiles);
        Assert.Null(limits.MaxOptionsPerSubProfile);
    }

    [Fact]
    public void List_value_limit_applies_only_when_set()
    {
        var current = ChoiceListRules.Resolve(Stages, null);
        var inputs = current.Values.Select(v => new ChoiceValueInput(v.Key, v.Label, null, v.Active)).Append(new ChoiceValueInput(null, "Yeni", "open")).ToList();

        Assert.Equal(3, ChoiceListRules.Apply(Stages, current, inputs).Count);
        Assert.Equal(3, ChoiceListRules.Apply(Stages, current, inputs, maxValues: 3).Count);
        var ex = Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, inputs, maxValues: 2));
        Assert.Equal("values", ex.ParamName);
    }

    private static SubProfileOption Option(SubProfile subProfile, int order, SubProfileOptionKind kind, string target, string label) =>
        new(subProfile.Id, order) { Kind = kind, TargetKey = target, Label = label };

    private sealed class CrmLike : IModule
    {
        public string Key => "crm";

        public string DisplayName => "CRM";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("crm.deals.read", "Okur")];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities { get; } = [new("crm.deal", "Fırsat")];

        public IReadOnlyList<ChoiceListDefinition> ChoiceLists { get; } = [Stages, Sources];
    }

    private sealed class Shipping : IModule
    {
        public string Key => "ship";

        public string DisplayName => "Sevkiyat";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("ship.loads.read", "Okur")];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities { get; } = [new("ship.load", "Yük")];
    }
}
