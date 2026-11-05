using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;

namespace Modulix.Core.Tests.Customization;

public class ChoiceListRulesTests
{
    private static readonly ChoiceListDefinition Stages = new(
        "crm.opportunity-stage",
        "Fırsat aşamaları",
        "crm.opportunity",
        "stage",
        Required: true,
        [new("open", "Açık"), new("won", "Kazanıldı"), new("lost", "Kaybedildi")],
        [new("Lead", "Aday", "open"), new("Won", "Kazanıldı", "won"), new("Lost", "Kaybedildi", "lost")]);

    [Fact]
    public void Without_edits_the_code_defaults_apply()
    {
        var list = ChoiceListRules.Resolve(Stages, null);

        Assert.Equal(["Lead", "Won", "Lost"], list.Values.Select(v => v.Key));
        Assert.All(list.Values, v => Assert.True(v.Active && v.BuiltIn));
        Assert.Equal("Lead", list.DefaultValue!.Key);
    }

    [Fact]
    public void Stored_order_is_kept_and_new_built_in_values_are_appended()
    {
        // Kod sonradan "Lost" değerini eklemiş gibi: saklı listede yok, sona etkin olarak gelir.
        var list = ChoiceListRules.Resolve(Stages, [new("Won", "Kazandık", "lost", true), new("Lead", "İlk temas", null, false)]);

        Assert.Equal(["Won:Kazandık:won", "Lead:İlk temas:open", "Lost:Kaybedildi:lost"], list.Values.Select(v => $"{v.Key}:{v.Label}:{v.Category}"));
        Assert.Equal("Won", list.DefaultValue!.Key);
        Assert.Equal(["Lead"], list.KeysIn("open"));
    }

    [Fact]
    public void New_value_gets_a_key_and_its_category_and_missing_values_become_inactive()
    {
        var current = ChoiceListRules.Resolve(Stages, null);

        var values = ChoiceListRules.Apply(Stages, current, [new(null, "Demo yapıldı", "open"), new("won", "Kazanıldı"), new("Lost", "Kaybedildi")]);

        Assert.Equal(["demo_yapildi:True", "Won:True", "Lost:True", "Lead:False"], values.Select(v => $"{v.Key}:{v.Active}"));
        Assert.Equal("open", values[0].Category);
    }

    [Fact]
    public void Generated_key_does_not_collide_with_a_built_in_key_in_another_case()
    {
        var current = ChoiceListRules.Resolve(Stages, null);

        var values = ChoiceListRules.Apply(Stages, current, [new("Lead", "Aday"), new(null, "Lead", "open"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi")]);

        Assert.Equal("lead_2", values[1].Key);
    }

    [Theory]
    [InlineData("won")]
    [InlineData("lost")]
    public void Every_category_keeps_an_active_value(string category)
    {
        var current = ChoiceListRules.Resolve(Stages, null);
        var inputs = current.Values.Select(v => new ChoiceValueInput(v.Key, v.Label, null, v.Category != category)).ToList();

        var error = Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, inputs));

        Assert.Equal("values", error.ParamName);
    }

    [Fact]
    public void Custom_value_added_before_categories_existed_can_be_categorized_once()
    {
        // Kategori tanımlanmadan önce eklenmiş özel değer: kategorisi boştur; bir kez atanabilir, sonra değişmez.
        var uncategorized = ChoiceListRules.Resolve(Stages, [new("Demo", "Demo yapıldı", null, true)]);
        Assert.Null(uncategorized.Find("Demo")!.Category);

        var values = ChoiceListRules.Apply(Stages, uncategorized, [new("Lead", "Aday"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"), new("Demo", "Demo yapıldı", "open")]);
        Assert.Equal("open", values.Single(v => v.Key == "Demo").Category);

        var categorized = ChoiceListRules.Resolve(Stages, values);
        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, categorized, [new("Lead", "Aday"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"), new("Demo", "Demo yapıldı", "won")]));

        // Tanımsız kategori atanamaz.
        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, uncategorized, [new("Lead", "Aday"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"), new("Demo", "Demo yapıldı", "uydurma")]));
    }

    [Fact]
    public void Built_in_category_cannot_change_and_new_value_needs_a_category()
    {
        var current = ChoiceListRules.Resolve(Stages, null);

        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, [new("Lead", "Aday", "won"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi")]));
        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, [new(null, "Yeni"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi")]));
        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, [new("Lead", "Aday"), new("Won", "aday"), new("Lost", "Kaybedildi")]));
        Assert.Throws<ArgumentException>(() => ChoiceListRules.Apply(Stages, current, [new("Uydurma", "Uydurma"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi")]));
    }
}
