using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;

namespace Modulix.Core.Tests.Customization;

/// <summary>ADR-0021: form düzeni çözümleme ve doğrulama kuralları (veritabanı yok).</summary>
public class FormLayoutRulesTests
{
    private static readonly FormDefinition Form = new(
        "crm.deal",
        "Fırsat",
        [new("genel", "Genel bilgiler")],
        [new("temel", "Temel bilgiler", "target"), new("ek", "Ek bilgiler")],
        [
            new("title", "Başlık", "temel", 12, Required: true, Fixed: true),
            new("amount", "Tutar", "temel", 4),
            new("notes", "Notlar", "temel", 12),
        ]);

    private static CustomFieldInfo Custom(string key, bool required = false, CustomFieldType type = CustomFieldType.Text) =>
        new(Guid.NewGuid(), "crm.deal", key, key.ToUpperInvariant(), type, required, false, 0, false, []);

    [Fact]
    public void Default_layout_follows_the_definition_and_puts_custom_fields_in_the_extras_section()
    {
        var layout = FormLayoutRules.Resolve(Form, [Custom("plaka"), Custom("notlar2", type: CustomFieldType.MultilineText)], stored: null, version: 0);

        Assert.False(layout.Customized);
        Assert.Equal(["genel"], layout.Tabs.Select(t => t.Key));
        Assert.Equal(["temel", "ek"], layout.Sections.Select(s => s.Key));
        Assert.Equal(["title", "amount", "notes", "custom.plaka", "custom.notlar2"], layout.Fields.Select(f => f.Key));
        Assert.All(layout.Fields, f => Assert.True(f.Visible));
        var title = layout.Fields.Single(f => f.Key == "title");
        Assert.True(title.Required && title.Fixed && title.DefaultRequired);
        var plaka = layout.Fields.Single(f => f.Key == "custom.plaka");
        Assert.True(plaka.IsCustom);
        Assert.Equal("ek", plaka.Section);
        Assert.Equal(6, plaka.Span);
        Assert.Equal(12, layout.Fields.Single(f => f.Key == "custom.notlar2").Span);
        Assert.Empty(FormLayoutRules.ExtraRequired(layout));
    }

    [Fact]
    public void Stored_layout_moves_fields_adds_requirements_and_keeps_fixed_fields_visible()
    {
        var stored = new FormLayoutInput(
            [new("genel", "Genel"), new("finans", "Finans")],
            [new("temel", "Temel", "target", "genel"), new("fin", "Finansal", "coins", "finans"), new("kayip", "Kayıp", null, "yok")],
            [
                new("amount", "fin", 6, Required: true, Visible: true),
                new("title", "temel", 12, Required: false, Visible: false),
                new("zzz", "temel", 6, Required: false, Visible: true),
                new("custom.plaka", "kayip", 99, Required: false, Visible: false),
            ]);

        var layout = FormLayoutRules.Resolve(Form, [Custom("plaka"), Custom("yeni", required: true)], stored, version: 3);

        Assert.True(layout.Customized);
        Assert.Equal(3, layout.Version);
        Assert.Equal(["genel", "finans"], layout.Tabs.Select(t => t.Key));
        // Sekmesi olmayan bölüm ilk sekmeye düşer.
        Assert.Equal("genel", layout.Sections.Single(s => s.Key == "kayip").Tab);

        var amount = layout.Fields.Single(f => f.Key == "amount");
        Assert.Equal("fin", amount.Section);
        Assert.True(amount.Required);
        Assert.False(amount.DefaultRequired);

        // Sabit alan gizlenemez, zorunluluğu azaltılamaz.
        var title = layout.Fields.Single(f => f.Key == "title");
        Assert.True(title.Visible && title.Required);

        // Bilinmeyen alan atlanır; geçersiz genişlik varsayılana döner; düzende olmayan alanlar sona eklenir.
        Assert.DoesNotContain(layout.Fields, f => f.Key == "zzz");
        Assert.Equal(6, layout.Fields.Single(f => f.Key == "custom.plaka").Span);
        Assert.False(layout.Fields.Single(f => f.Key == "custom.plaka").Visible);
        Assert.Equal(["amount", "title", "custom.plaka", "notes", "custom.yeni"], layout.Fields.Select(f => f.Key));
        // Varsayılan bölümü ("ek") düzende yoksa ilk bölüme düşer.
        Assert.Equal("temel", layout.Fields.Single(f => f.Key == "custom.yeni").Section);

        Assert.Equal(["amount"], FormLayoutRules.ExtraRequired(layout).Order());
    }

    [Fact]
    public void Validation_rejects_unknown_missing_and_invalid_placements()
    {
        var custom = new[] { Custom("plaka") };
        FormLayoutInput Valid(params FormFieldInput[] overrides)
        {
            var fields = new List<FormFieldInput>
            {
                new("title", "temel", 12, true, true), new("amount", "temel", 4, false, true), new("notes", "temel", 12, false, true), new("custom.plaka", "ek", 6, false, true),
            };
            foreach (var o in overrides)
            {
                var i = fields.FindIndex(f => f.Key == o.Key);
                if (i >= 0)
                {
                    fields[i] = o;
                }
                else
                {
                    fields.Add(o);
                }
            }

            return new FormLayoutInput([new("genel", "Genel")], [new("temel", "Temel", "target", "genel"), new("ek", "Ek", null, "genel")], fields);
        }

        Assert.Empty(FormLayoutRules.Validate(Form, custom, Valid()));

        Assert.Contains(FormLayoutRules.Validate(Form, custom, Valid(new FormFieldInput("zzz", "temel", 6, false, true))), e => e.Message.Contains("Bilinmeyen alan"));
        Assert.Contains(FormLayoutRules.Validate(Form, custom, Valid(new FormFieldInput("amount", "temel", 5, false, true))), e => e.Field == "fields[1].span");
        Assert.Contains(FormLayoutRules.Validate(Form, custom, Valid(new FormFieldInput("amount", "yok", 4, false, true))), e => e.Field == "fields[1].section");
        Assert.Contains(FormLayoutRules.Validate(Form, custom, Valid(new FormFieldInput("title", "temel", 12, true, false))), e => e.Message.Contains("Sabit alan"));
        Assert.Contains(FormLayoutRules.Validate(Form, custom, Valid(new FormFieldInput("title", "temel", 12, false, true))), e => e.Message.Contains("Sabit alan"));

        var missing = new FormLayoutInput([new("genel", "Genel")], [new("temel", "Temel", null, "genel")], [new("title", "temel", 12, true, true)]);
        Assert.Contains(FormLayoutRules.Validate(Form, custom, missing), e => e.Field == "fields" && e.Message.Contains("eksik"));

        var badSection = new FormLayoutInput([new("genel", "Genel")], [new("temel", "Temel", null, "baska")], Valid().Fields);
        Assert.Contains(FormLayoutRules.Validate(Form, custom, badSection), e => e.Field == "sections[0].tab");

        var badKey = new FormLayoutInput([new("Genel Sekme", "Genel")], [new("temel", "Temel", null, "Genel Sekme")], Valid().Fields);
        Assert.Contains(FormLayoutRules.Validate(Form, custom, badKey), e => e.Field == "tabs[0].key");

        var noTabs = new FormLayoutInput([], [new("temel", "Temel", null, "genel")], Valid().Fields);
        Assert.Contains(FormLayoutRules.Validate(Form, custom, noTabs), e => e.Field == "tabs");
    }
}
