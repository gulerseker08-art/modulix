using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 12): Destek modülünün etiket tabloları (<see cref="SupportLabels"/>, <see cref="SlaLabels"/>, <see cref="ReportLabels"/>)
/// sözlükten okunur. Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve
/// Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class SupportLabelsTests
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

    // Sözlük testte yüklenmediği için tablolar kaynak metni döndürür.
    private static IEnumerable<(string Group, IReadOnlyList<(string Key, string Label)> Rows)> Tables() =>
    [
        (SupportLabels.StatusGroup, SupportLabels.Statuses),
        (SupportLabels.PriorityGroup, SupportLabels.Priorities),
        (SupportLabels.LevelGroup, SupportLabels.Levels),
        (SupportLabels.ChannelGroup, SupportLabels.Channels),
        (SupportLabels.SortGroup, SupportLabels.Sorts),
        (SupportLabels.MessageKindGroup, SupportLabels.MessageKinds),
        (SupportLabels.ActionGroup, SupportLabels.Actions),
        (SupportLabels.HistoryGroup, SupportLabels.Histories),
        (SupportLabels.DeliveryGroup, SupportLabels.Deliveries),
        (SlaLabels.MetricGroup, SlaLabels.Metrics),
        (SlaLabels.ActionGroup, SlaLabels.Actions),
        (SlaLabels.StateGroup, SlaLabels.States),
        (SlaLabels.DayGroup, SlaLabels.DayRows),
        (SlaLabels.ShortDayGroup, SlaLabels.ShortDays),
        (SlaLabels.HistoryGroup, SlaLabels.Histories),
        (ReportLabels.GranularityGroup, ReportLabels.Granularities),
    ];

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Tables()
            .SelectMany(t => t.Rows.Select(r => SupportLabels.KeyOf(t.Group, r.Key)))
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Tables()
            .SelectMany(t => t.Rows.Select(r => (Key: SupportLabels.KeyOf(t.Group, r.Key), r.Label)))
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Duration_texts_exist_in_each_language(string language)
    {
        var texts = Dictionary(language);
        string[] keys =
        [
            "support.duration.days", "support.duration.hours", "support.duration.minutes", "support.duration.left", "support.duration.over",
            "support.duration.dayHour", "support.duration.hourMinute", "support.percent",
        ];

        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(texts.GetValueOrDefault(key)), key));
    }

    [Fact]
    public void Lookups_return_the_source_text_for_known_keys_and_the_key_otherwise()
    {
        Assert.Equal("Üzerime al", SupportLabels.Action("take"));
        Assert.Equal("unknown-action", SupportLabels.Action("unknown-action"));
        Assert.Equal("Talep açıldı", SupportLabels.History("Created"));
        Assert.Equal("SLA hedefi aşıldı", SlaLabels.HistoryKind("SlaBreached"));
        Assert.Equal("Created", SlaLabels.HistoryKind("Created"));
        Assert.Equal(string.Empty, SupportLabels.Delivery("Unknown"));
        Assert.Equal("Gönderildi", SupportLabels.Delivery("Sent"));
        Assert.Equal("Paz", SlaLabels.ShortDay(0));
        Assert.Equal("Cmt", SlaLabels.ShortDay(6));
        Assert.Equal("Günlük", ReportLabels.Granularity("other"));
        Assert.Equal("Çözüm", ReportLabels.Metric("Resolution"));
        Assert.Equal((0, "Pazar"), SlaLabels.Days[^1]);
    }

    [Fact]
    public void Formatted_texts_fall_back_to_the_turkish_source()
    {
        Assert.Equal("1 gün 2 sa 5 dk", SlaLabels.Duration(1565));
        Assert.Equal("1 g 2 sa", ReportLabels.Minutes(1565));
        Assert.Equal("support.sort.desc-createdat", SupportLabels.KeyOf(SupportLabels.SortGroup, "-createdAt"));
    }
}
