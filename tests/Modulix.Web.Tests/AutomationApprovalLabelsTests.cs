using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 16b): Otomasyon ve onay ekranlarının etiket tabloları (<see cref="AutomationLabels"/>, <see cref="ApprovalLabels"/>)
/// sözlükten okunur. Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu,
/// Türkçe sözlüğün kaynak metinle aynı kaldığını ve birleştirilmiş Türkçe metinlerin eski sabit metinle aynı çıktığını denetler.
/// </summary>
public class AutomationApprovalLabelsTests
{
    private static Dictionary<string, string> Dictionary(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", $"{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    private static IEnumerable<(string Group, IReadOnlyList<(string Key, string Label)> Rows)> Tables() =>
    [
        (AutomationLabels.StatusGroup, AutomationLabels.StatusLabels),
        (AutomationLabels.RunStatusGroup, AutomationLabels.RunStatusLabels),
        (AutomationLabels.StepStatusGroup, AutomationLabels.StepStatusLabels),
        (AutomationLabels.OriginGroup, AutomationLabels.OriginLabels),
        (AutomationLabels.TriggerGroup, AutomationLabels.TriggerLabels),
        (AutomationLabels.OperatorGroup, AutomationLabels.OperatorLabels),
        (AutomationLabels.LogicGroup, AutomationLabels.LogicLabels),
        (AutomationLabels.BehaviourGroup, AutomationLabels.BehaviourLabels),
        (ApprovalLabels.StatusGroup, ApprovalLabels.Statuses),
    ];

    private static IEnumerable<(string Key, string Label)> Rows() =>
        Tables().SelectMany(t => t.Rows.Select(r => (AdminLabels.KeyOf(t.Group, r.Key), r.Label)));

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Rows()
            .Select(r => r.Key)
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Rows()
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Tables_cover_every_value_the_screens_offer()
    {
        Assert.Equal(AutomationLabels.Statuses, AutomationLabels.StatusLabels.Select(s => s.Key));
        Assert.Equal(AutomationLabels.RunStatuses, AutomationLabels.RunStatusLabels.Select(s => s.Key));
        Assert.Equal(EditorTrigger.Types.Order(), AutomationLabels.TriggerLabels.Select(s => s.Key).Order());
        Assert.Equal(5, ApprovalLabels.Statuses.Count);
    }

    [Fact]
    public void Lookups_return_the_source_text_and_fall_back_to_the_technical_name()
    {
        Assert.Equal("ile başlar", AutomationLabels.Operator("StartsWith"));
        Assert.Equal("Bilinmeyen", AutomationLabels.Operator("Bilinmeyen"));
        Assert.Equal("Herhangi biri sağlanırsa", AutomationLabels.Logic(EditorCondition.Or));
        Assert.Equal("Hepsi sağlanırsa", AutomationLabels.Logic("tanımsız"));
        Assert.Equal("İş olayı", AutomationLabels.TriggerType(EditorTrigger.DomainEvent));
        Assert.Equal("Geri çekildi", ApprovalLabels.StatusText("Withdrawn"));
        Assert.Equal("Unknown", ApprovalLabels.StatusText("Unknown"));
        Assert.Equal("admin.automation.operator.startswith", AdminLabels.KeyOf(AutomationLabels.OperatorGroup, "startsWith"));
        Assert.Equal("Kayıt 3f9a1c2e", RecordPickerLabels.ShortId(Guid.Parse("3f9a1c2e-0000-0000-0000-000000000000")));
    }

    [Fact]
    public void Turkish_dictionary_reproduces_the_composed_texts()
    {
        // Birleştirilmiş metinler Türkçe sözlükle eski sabit metnin verdiği sonucu üretmelidir.
        var texts = Dictionary("tr");
        Assert.Equal("Yayınlanmış sürüm (v3)", string.Format(texts["admin.automationRule.published"], 3));
        Assert.Equal("4 adımdan 2 tanesi onaylandı", string.Format(texts["approvals.center.progress"], 4, 2));
        Assert.Equal("5 talep kararınızı bekliyor", string.Format(texts["approvals.center.hero.waiting"], "5"));
        Assert.Equal("Adım 2 koşulu", string.Format(texts["admin.automationEditor.stepCondition"], 2));
        Assert.Equal("Tutar: değişken ekle", string.Format(texts["admin.automationEditor.addVariableLabel"], "Tutar"));
        Assert.Equal("Daha fazla göster (10 / 24)", string.Format(texts["lookup.showMore"], 10, 24));
        Assert.Equal(
            "<a href=\"x\">Hazır şablonlardan</a> başlayabilirsiniz.",
            string.Format(texts["admin.automationRules.emptyTemplates"], "<a href=\"x\">", "</a>"));
    }
}
