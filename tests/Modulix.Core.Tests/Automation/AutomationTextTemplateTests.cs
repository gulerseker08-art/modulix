using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Metin parametrelerinde kayıt değişkeni {{alan}} (ADR-0027, Dilim 4d): biçim, beyaz liste, doldurma, yetki, enjeksiyon.</summary>
public class AutomationTextTemplateTests
{
    private static readonly AutomationFieldSet Visible = Fields(SecretPermission);
    private static readonly AutomationFieldSet Hidden = Fields();

    private static string Render(string text, AutomationFieldSet fields, JsonObject values, List<string>? notes = null, bool encodeHtml = false) =>
        AutomationTextTemplate.Render(text, fields, Facts(fields, values), notes ?? [], encodeHtml);

    [Fact]
    public void Variables_are_listed_once_in_order_and_tolerate_spaces()
    {
        var text = "Merhaba {{ sales.deal.title }}, {{sales.deal.stage}} / {{sales.deal.title}}";

        Assert.Equal([Title.Key, Stage.Key], AutomationTextTemplate.Variables(text));
        Assert.True(AutomationTextTemplate.HasVariables(text));
        Assert.False(AutomationTextTemplate.HasVariables("süsüz metin { } ve tek {parantez}"));
        Assert.Empty(AutomationTextTemplate.Errors(text));
    }

    [Theory]
    [InlineData("Kapanmayan {{sales.deal.title")]
    [InlineData("Açılmayan sales.deal.title}} burada")]
    [InlineData("Boşluklu {{sales deal}} ad")]
    [InlineData("İfade {{1 + 1}}")]
    [InlineData("Boş {{}} değişken")]
    public void Malformed_variables_are_reported(string text) =>
        Assert.Single(AutomationTextTemplate.Errors(text));

    [Fact]
    public void Only_readable_known_text_like_fields_may_enter_a_text()
    {
        Assert.Null(AutomationTextTemplate.CheckVariable(Visible, Title.Key));
        Assert.Null(AutomationTextTemplate.CheckVariable(Visible, Secret.Key));
        Assert.Contains("Bilinmeyen", AutomationTextTemplate.CheckVariable(Visible, "sales.deal.nope"));
        Assert.Contains("izniniz yok", AutomationTextTemplate.CheckVariable(Hidden, Secret.Key));
        Assert.Contains("kayıt seçimi", AutomationTextTemplate.CheckVariable(Visible, Owner.Key));
        Assert.Contains("tarih ve saat", AutomationTextTemplate.CheckVariable(Visible, LastContact.Key));
    }

    [Fact]
    public void Values_are_formatted_for_reading_by_field_type()
    {
        var values = new JsonObject
        {
            ["title"] = "Anka Lojistik",
            ["stage"] = "won",
            ["amount"] = 1234.5m,
            ["currency"] = "try",
            ["score"] = 7.5m,
            ["isVip"] = true,
            ["dueDate"] = "2026-11-07",
        };

        var text = Render(
            "{{sales.deal.title}} | {{sales.deal.stage}} | {{sales.deal.amount}} | {{sales.deal.score}} | {{sales.deal.isVip}} | {{sales.deal.dueDate}}",
            Visible,
            values);

        Assert.Equal("Anka Lojistik | Kazanıldı | 1.234,50 TRY | 7,5 | Evet | 07.11.2026", text);
    }

    [Fact]
    public void An_empty_or_hidden_value_becomes_empty_text_with_a_note_that_never_shows_the_value()
    {
        var notes = new List<string>();
        var values = new JsonObject { ["title"] = null, ["margin"] = 42 };

        var text = Render("[{{sales.deal.title}}][{{sales.deal.margin}}]", Hidden, values, notes);

        Assert.Equal("[][]", text);
        Assert.Equal(2, notes.Count);
        Assert.Contains("boş", notes[0]);
        Assert.Contains("görülemiyor", notes[1]);
        Assert.DoesNotContain(notes, n => n.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_variable_at_run_time_is_left_empty_with_a_note()
    {
        var notes = new List<string>();

        Assert.Equal("x", Render("x{{sales.deal.nope}}", Visible, new JsonObject(), notes));
        Assert.Contains("kullanılamıyor", Assert.Single(notes));
    }

    [Fact]
    public void A_value_that_looks_like_a_variable_is_not_expanded_again()
    {
        var values = new JsonObject { ["title"] = "{{sales.deal.margin}}", ["margin"] = 42 };

        var text = Render("Konu: {{sales.deal.title}}", Visible, values);

        Assert.Equal("Konu: {{sales.deal.margin}}", text);
    }

    [Fact]
    public void Html_encoding_applies_to_values_only_and_only_when_asked()
    {
        var values = new JsonObject { ["title"] = "<b>\"Anka\" & Ortakları</b>" };

        Assert.Equal("<i><b>\"Anka\" & Ortakları</b></i>", Render("<i>{{sales.deal.title}}</i>", Visible, values));
        Assert.Equal("<i>&lt;b&gt;&quot;Anka&quot; &amp; Ortakları&lt;/b&gt;</i>", Render("<i>{{sales.deal.title}}</i>", Visible, values, encodeHtml: true));
    }

    // Çözümleyici: önizleme ve çalışma aynı yolu kullanır.

    private static AutomationActionStep Step(JsonObject parameters) => new("s1", FollowUp.Key, parameters);

    [Fact]
    public void The_resolver_fills_a_bindable_text_parameter_and_keeps_other_literals()
    {
        var facts = Facts(Visible, new JsonObject { ["title"] = "Anka Lojistik" });
        var step = Step(new JsonObject { ["subject"] = "{{sales.deal.title}} ile görüşün", ["priority"] = "high", ["dueInDays"] = 2 });

        var resolved = AutomationParameterResolver.Resolve(step, Visible, facts, FollowUp);

        Assert.Equal("Anka Lojistik ile görüşün", resolved.Values["subject"]!.GetValue<string>());
        Assert.Equal("high", resolved.Values["priority"]!.GetValue<string>());
        Assert.Equal(2, resolved.Values["dueInDays"]!.GetValue<int>());
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void A_text_parameter_that_does_not_take_record_data_keeps_the_braces_literally()
    {
        // "note" kayıttan bağlanamaz (AllowFieldBinding = false): doğrulayıcı yayını reddeder, çözümleyici de yorumlamaz.
        var facts = Facts(Visible, new JsonObject { ["title"] = "Anka" });
        var step = Step(new JsonObject { ["note"] = "{{sales.deal.title}}" });

        var resolved = AutomationParameterResolver.Resolve(step, Visible, facts, FollowUp);

        Assert.Equal("{{sales.deal.title}}", resolved.Values["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_rendered_text_longer_than_the_parameter_limit_is_shortened_with_a_note()
    {
        var facts = Facts(Visible, new JsonObject { ["title"] = new string('a', 600) });
        var step = Step(new JsonObject { ["subject"] = "{{sales.deal.title}}" });

        var resolved = AutomationParameterResolver.Resolve(step, Visible, facts, FollowUp);

        var subject = resolved.Values["subject"]!.GetValue<string>();
        Assert.Equal(500, subject.Length);
        Assert.EndsWith("…", subject);
        Assert.Contains("kısaltıldı", Assert.Single(resolved.Notes));
    }

    [Fact]
    public void A_subject_that_renders_empty_counts_as_a_missing_required_parameter()
    {
        var facts = Facts(Visible, new JsonObject { ["title"] = null });
        var step = Step(new JsonObject { ["subject"] = "{{sales.deal.title}}", ["dueInDays"] = 1 });

        var resolved = AutomationParameterResolver.Resolve(step, Visible, facts, FollowUp);

        Assert.Equal(["Konu"], AutomationParameterResolver.MissingRequired(FollowUp, resolved.Values));
    }

    // Doğrulayıcı: yayında değişken denetlenir.

    private static IReadOnlyList<AutomationIssue> Validate(JsonObject parameters, string[]? permissions = null)
    {
        var access = new FakeAccess(permissions ?? ["sales.deals.read", WritePermission, SecretPermission], ["sales", "billing"]);
        var definition = new AutomationDefinition(new ManualTrigger(), null, [new AutomationActionStep("step-1", FollowUp.Key, parameters)]);
        return Validator(Catalog(), access).ValidateAsync("sales", Entity, definition, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static JsonObject Subject(string text) => new() { ["subject"] = text, ["dueInDays"] = 1 };

    [Fact]
    public void Publishing_accepts_known_readable_variables()
    {
        Assert.Empty(Validate(Subject("{{sales.deal.title}} için {{ sales.deal.stage }} takibi")));
    }

    [Fact]
    public void Publishing_rejects_unknown_malformed_and_unfit_variables()
    {
        Assert.Contains(Validate(Subject("{{sales.deal.nope}}")), i => i.Message.Contains("Bilinmeyen değişken"));
        Assert.Contains(Validate(Subject("{{sales.deal.title")), i => i.Message.Contains("Kapanmamış"));
        Assert.Contains(Validate(Subject("{{sales.deal.ownerId}}")), i => i.Message.Contains("kayıt seçimi"));
        Assert.Contains(Validate(Subject("{{sales.deal.lastContactAt}}")), i => i.Message.Contains("tarih ve saat"));
    }

    [Fact]
    public void Publishing_rejects_a_variable_the_caller_may_not_read()
    {
        var issues = Validate(Subject("Marj {{sales.deal.margin}}"), permissions: ["sales.deals.read", WritePermission]);

        Assert.Contains(issues, i => i.Path == "actions[0].parameters.subject" && i.Message.Contains("izniniz yok"));
    }

    [Fact]
    public void Publishing_rejects_variables_in_a_parameter_that_does_not_take_record_data()
    {
        var parameters = Subject("x");
        parameters["note"] = "Sahibi {{sales.deal.title}}";

        Assert.Contains(Validate(parameters), i => i.Path == "actions[0].parameters.note" && i.Message.Contains("kabul etmez"));
    }
}
