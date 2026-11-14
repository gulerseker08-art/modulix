using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 13): Pazarlama modülünün etiket tabloları (<see cref="MarketingLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak
/// (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class MarketingLabelsTests
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
        (MarketingLabels.TargetGroup, MarketingLabels.Targets),
        (MarketingLabels.KindGroup, MarketingLabels.Kinds),
        (MarketingLabels.StatusGroup, MarketingLabels.Statuses),
        (MarketingLabels.OperatorGroup, MarketingLabels.Operators),
        (MarketingLabels.ResponseKindGroup, MarketingLabels.ResponseKinds),
        (MarketingLabels.ResponseStatusGroup, MarketingLabels.ResponseStatuses),
        (MarketingLabels.ChannelGroup, MarketingLabels.Channels),
        (MarketingLabels.CampaignStatusGroup, MarketingLabels.CampaignStatuses),
        (MarketingLabels.RecipientRuleGroup, MarketingLabels.RecipientRules),
        (MarketingLabels.RoleGroup, MarketingLabels.Roles),
        (MarketingLabels.ReasonGroup, MarketingLabels.Reasons),
        (MarketingLabels.RunStatusGroup, MarketingLabels.RunStatuses),
        (MarketingLabels.DeliveryStatusGroup, MarketingLabels.DeliveryStatuses),
        (MarketingLabels.DeliveryCodeGroup, MarketingLabels.DeliveryCodes),
        (MarketingLabels.RecipientStatusGroup, MarketingLabels.RecipientStatuses),
    ];

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Tables()
            .SelectMany(t => t.Rows.Select(r => MarketingLabels.KeyOf(t.Group, r.Key)))
            .Append("marketing.nullNote")
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Tables()
            .SelectMany(t => t.Rows.Select(r => (Key: MarketingLabels.KeyOf(t.Group, r.Key), r.Label)))
            .Append((Key: "marketing.nullNote", Label: MarketingLabels.NullNote))
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Turkish_labels_do_not_use_the_word_lead()
    {
        Assert.DoesNotContain(Tables().SelectMany(t => t.Rows), r => r.Label.Contains("Lead", StringComparison.Ordinal));
        Assert.Equal("Müşteri adayı", MarketingLabels.Label(MarketingLabels.Targets, "Lead"));
    }

    [Fact]
    public void Lookups_return_the_source_text_for_known_keys_and_the_key_otherwise()
    {
        Assert.Equal("eşittir", MarketingLabels.Operator("eq"));
        Assert.Equal("xyz", MarketingLabels.Operator("xyz"));
        Assert.Equal("Görüşme talebi", MarketingLabels.ResponseKind("MeetingRequest"));
        Assert.Equal("Müşteri adayına dönüştü", MarketingLabels.ResponseStatus("Converted"));
        Assert.Equal("Odd", MarketingLabels.ResponseStatus("Odd"));
        Assert.Equal("Sağlayıcıya kabul edildi", MarketingLabels.DeliveryStatus("Accepted"));
        Assert.Equal("Günlük ileti sınırı doldu", MarketingLabels.DeliveryReason("frequency-limit"));
        Assert.Equal("Engelli: elle engellenmiş", MarketingLabels.DeliveryReason("suppressed:manual"));
        Assert.Equal("bilinmeyen-kod", MarketingLabels.Reason("bilinmeyen-kod"));
        Assert.Equal("Uygun", MarketingLabels.RecipientStatus("Eligible"));
        Assert.Equal("Alıcı yok", MarketingLabels.RecipientStatus("Other"));
        Assert.Equal("marketing.reason.suppressed-hard-bounce", MarketingLabels.KeyOf(MarketingLabels.ReasonGroup, "suppressed:hard-bounce"));
        Assert.Equal("crm.role.purchasing", MarketingLabels.KeyOf(MarketingLabels.RoleGroup, "Purchasing"));
    }
}
