using System.Text.Json;
using Modulix.Shared.Crm;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034: aktivite durum/öncelik etiketleri <c>crm.act.*</c> anahtarlarından gelir. Türkçe metin kaynaktır (önceki sabit
/// etiketlerle aynı); her aile/durum çiftinin anahtarı üç sözlükte vardır. Anahtar eşitliğini mimari testler ayrıca denetler.
/// </summary>
public class ActivityLabelsTests
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

    private static readonly string[] Families =
        [ActivityFamily.Task, ActivityFamily.Call, ActivityFamily.Meeting, ActivityFamily.Visit, ActivityFamily.Email];

    private static readonly string[] Statuses =
    [
        ActivityStatus.Open, ActivityStatus.InProgress, ActivityStatus.Blocked, ActivityStatus.Completed, ActivityStatus.NoAnswer,
        ActivityStatus.NoShow, ActivityStatus.Cancelled, ActivityStatus.Draft, ActivityStatus.Queued, ActivityStatus.Sent, ActivityStatus.Failed,
    ];

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_family_and_status_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = new List<string>();
        foreach (var family in Families)
        {
            foreach (var status in Statuses)
            {
                // "Ulaşılamadı" yalnızca aramada, "Katılım olmadı" yalnızca toplantı ve ziyarette geçerlidir.
                if ((status == ActivityStatus.NoAnswer && family != ActivityFamily.Call)
                    || (status == ActivityStatus.NoShow && family is not (ActivityFamily.Meeting or ActivityFamily.Visit)))
                {
                    continue;
                }

                if (ActivityLabels.StatusKey(family, status) is not { } key)
                {
                    missing.Add($"anahtarsız durum: {family}/{status}");
                }
                else if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add($"{language}: {key}");
                }
            }
        }

        foreach (var priority in new[] { ActivityPriority.Low, ActivityPriority.Normal, ActivityPriority.High, ActivityPriority.Urgent })
        {
            if (!texts.ContainsKey(ActivityLabels.PriorityKey(priority)))
            {
                missing.Add($"{language}: {ActivityLabels.PriorityKey(priority)}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData(ActivityFamily.Call, ActivityStatus.Open, "Planlandı")]
    [InlineData(ActivityFamily.Call, ActivityStatus.Completed, "Gerçekleştirildi")]
    [InlineData(ActivityFamily.Call, ActivityStatus.NoAnswer, "Ulaşılamadı")]
    [InlineData(ActivityFamily.Meeting, ActivityStatus.Open, "Planlandı")]
    [InlineData(ActivityFamily.Visit, ActivityStatus.Completed, "Gerçekleşti")]
    [InlineData(ActivityFamily.Meeting, ActivityStatus.NoShow, "Katılım olmadı")]
    [InlineData(ActivityFamily.Task, ActivityStatus.Open, "Açık")]
    [InlineData(ActivityFamily.Task, ActivityStatus.Completed, "Tamamlandı")]
    [InlineData(ActivityFamily.Task, ActivityStatus.InProgress, "Devam ediyor")]
    [InlineData(ActivityFamily.Task, ActivityStatus.Blocked, "Bloke")]
    [InlineData(ActivityFamily.Call, ActivityStatus.Cancelled, "İptal edildi")]
    [InlineData(ActivityFamily.Email, ActivityStatus.Draft, "Taslak")]
    [InlineData(ActivityFamily.Email, ActivityStatus.Queued, "Kuyruğa alındı")]
    [InlineData(ActivityFamily.Email, ActivityStatus.Sent, "Gönderildi")]
    [InlineData(ActivityFamily.Email, ActivityStatus.Failed, "Başarısız")]
    public void Turkish_status_text_matches_the_previous_fixed_labels(string family, string status, string expected)
    {
        var key = ActivityLabels.StatusKey(family, status);

        Assert.NotNull(key);
        Assert.Equal(expected, Dictionary("tr")[key!]);
    }

    [Theory]
    [InlineData(ActivityPriority.Low, "Düşük")]
    [InlineData(ActivityPriority.Normal, "Normal")]
    [InlineData(ActivityPriority.High, "Yüksek")]
    [InlineData(ActivityPriority.Urgent, "Acil")]
    public void Turkish_priority_text_matches_the_previous_fixed_labels(string priority, string expected) =>
        Assert.Equal(expected, Dictionary("tr")[ActivityLabels.PriorityKey(priority)]);

    [Fact]
    public void Unknown_status_has_no_key_and_is_shown_as_is() =>
        Assert.Null(ActivityLabels.StatusKey(ActivityFamily.Task, "mystery"));

    [Theory]
    [InlineData("tr", "tr-TR")]
    [InlineData("en", "en-GB")]
    [InlineData("az", "az-Latn-AZ")]
    public void Ui_language_has_a_culture_for_month_and_day_names(string language, string expected) =>
        Assert.Equal(expected, UiLanguage.CultureOf(language).Name);
}
