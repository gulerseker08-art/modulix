using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 15): Rapor merkezinin etiket tabloları (<see cref="ReportCenterLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak
/// (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class ReportCenterLabelsTests
{
    // Tablo dışı yardımcı metinler; Türkçe kaynakları yardımcıdaki yedek metinlerdir.
    private static readonly (string Key, string Label)[] HelperTexts =
    [
        ("reports.aggregation.count", "Kayıt sayısı"), ("reports.visibility-badge.usersCount", "Seçilen kullanıcılar ({0})"),
        ("reports.schedule-status.never", "Henüz çalışmadı"), ("reports.cell.empty", "(boş)"), ("reports.allRecords", "Tüm kayıtlar"),
    ];

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
        (ReportCenterLabels.OperatorGroup, ReportCenterLabels.Operators),
        (ReportCenterLabels.AggregationGroup, ReportCenterLabels.Aggregations),
        (ReportCenterLabels.ChartGroup, ReportCenterLabels.Charts),
        (ReportCenterLabels.GrainGroup, ReportCenterLabels.Grains),
        (ReportCenterLabels.VisibilityGroup, ReportCenterLabels.Visibilities),
        (ReportCenterLabels.VisibilityBadgeGroup, ReportCenterLabels.VisibilityBadges),
        (ReportCenterLabels.DirectionGroup, ReportCenterLabels.Directions),
        (ReportCenterLabels.HealthGroup, ReportCenterLabels.Healths),
        (ReportCenterLabels.KindGroup, ReportCenterLabels.Kinds),
        (ReportCenterLabels.FrequencyGroup, ReportCenterLabels.Frequencies),
        (ReportCenterLabels.WeekdayGroup, ReportCenterLabels.Weekdays),
        (ReportCenterLabels.ExportSourceGroup, ReportCenterLabels.ExportSources),
        (ReportCenterLabels.ScheduleStatusGroup, ReportCenterLabels.ScheduleStatuses),
        (ReportCenterLabels.DaySuffixGroup, ReportCenterLabels.DaySuffixes),
    ];

    private static IEnumerable<(string Key, string Label)> Rows() =>
        Tables().SelectMany(t => t.Rows.Select(r => (ReportCenterLabels.KeyOf(t.Group, r.Key), r.Label))).Concat(HelperTexts);

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
    public void Every_day_of_month_that_can_be_scheduled_has_a_suffix()
    {
        Assert.Equal(Enumerable.Range(1, 28).Select(d => d.ToString(System.Globalization.CultureInfo.InvariantCulture)), ReportCenterLabels.DaySuffixes.Select(s => s.Key));
    }

    [Fact]
    public void Lookups_return_the_source_text_for_known_keys_and_the_key_otherwise()
    {
        Assert.Equal("büyük veya eşittir", ReportCenterLabels.Operator("greaterOrEqual"));
        Assert.Equal("Kayıt sayısı", ReportCenterLabels.Aggregation("count"));
        Assert.Equal("Ortalama", ReportCenterLabels.Aggregation("average"));
        Assert.Equal("Ay", ReportCenterLabels.Grain(null));
        Assert.Equal("Seçilen kullanıcılar (3)", ReportCenterLabels.Visibility("users", 3));
        Assert.Equal("Özel", ReportCenterLabels.Visibility("private"));
        Assert.Equal("Veri kümesi yok", ReportCenterLabels.Health("datasetMissing"));
        Assert.Equal("Tarih ve saat", ReportCenterLabels.Kind("dateTime"));
        Assert.Equal("Teslim edildi", ReportCenterLabels.ScheduleStatus("Delivered"));
        Assert.Equal("Henüz çalışmadı", ReportCenterLabels.ScheduleStatus(null));
        Assert.Equal("Queued", ReportCenterLabels.ScheduleStatus("Queued"));
        Assert.Equal("unknown", ReportCenterLabels.Kind("unknown"));
        Assert.Equal("reports.operator.notequals", ReportCenterLabels.KeyOf(ReportCenterLabels.OperatorGroup, "notEquals"));
    }

    [Fact]
    public void Turkish_dictionary_reproduces_the_schedule_summaries()
    {
        // Sözlük testte yüklenmez; özet biçimleri Türkçe sözlükteki metinle, eski sabit metnin verdiği sonucu üretmelidir.
        var texts = Dictionary("tr");
        Assert.Equal("Her gün 09:00", string.Format(texts["reports.schedule.daily"], ReportCenterLabels.Hour(9)));
        Assert.Equal("Her pazartesi 07:00", string.Format(texts["reports.schedule.weekly"], texts["reports.weekday.monday"], ReportCenterLabels.Hour(7)));
        Assert.Equal("Her ayın 15'i 06:00", string.Format(texts["reports.schedule.monthlyDay"], 15, texts["reports.day-suffix.15"], ReportCenterLabels.Hour(6)));
        Assert.Equal("Her ay 06:00", string.Format(texts["reports.schedule.monthly"], ReportCenterLabels.Hour(6)));
    }
}
