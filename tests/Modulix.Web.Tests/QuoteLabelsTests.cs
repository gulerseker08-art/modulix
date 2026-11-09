using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034: teklif durum/onay/gönderim/kalem/rol/indirim/periyot adları, eylem ve olay adları <c>crm.quote-*</c> anahtarlarından gelir.
/// Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe
/// sözlüğün kaynakla aynı kaldığını denetler. Anahtar kümesi eşitliğini mimari testler ayrıca denetler.
/// </summary>
public class QuoteLabelsTests
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
        (QuoteLabels.StatusGroup, QuoteLabels.Statuses),
        (QuoteLabels.ApprovalGroup, QuoteLabels.Approvals),
        (QuoteLabels.DeliveryGroup, QuoteLabels.Deliveries),
        (QuoteLabels.LineKindGroup, QuoteLabels.LineKinds),
        (QuoteLabels.RoleGroup, QuoteLabels.Roles),
        (QuoteLabels.DiscountKindGroup, QuoteLabels.DiscountKinds),
        (QuoteLabels.PeriodGroup, QuoteLabels.Periods),
    ];

    private static string KeyOf(string group, string key) => $"{group}.{(key.Length == 0 ? "onetime" : key.ToLowerInvariant())}";

    private static readonly string[] Actions =
    [
        "submit-for-approval", "withdraw-approval", "approve", "reject-approval", "request-changes", "present", "accept", "reject", "cancel",
    ];

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = new List<string>();
        foreach (var (group, rows) in Tables())
        {
            foreach (var (key, _) in rows)
            {
                if (!texts.TryGetValue(KeyOf(group, key), out var text) || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add($"{language}: {KeyOf(group, key)}");
                }
            }
        }

        foreach (var action in Actions)
        {
            if (!texts.ContainsKey($"{QuoteLabels.ActionGroup}.{action}"))
            {
                missing.Add($"{language}: {QuoteLabels.ActionGroup}.{action}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        // Sözlük yüklü değilken etiketler kaynak metindir; Türkçe sözlük bunlarla aynı olmalı (dil değiştirmeyen kullanıcı için görünür değişiklik yok).
        var texts = Dictionary("tr");
        var different = new List<string>();
        foreach (var (group, rows) in Tables())
        {
            foreach (var (key, label) in rows)
            {
                if (texts[KeyOf(group, key)] != label)
                {
                    different.Add($"{KeyOf(group, key)}: {texts[KeyOf(group, key)]} != {label}");
                }
            }
        }

        foreach (var action in Actions)
        {
            if (texts[$"{QuoteLabels.ActionGroup}.{action}"] != QuoteLabels.Action(action))
            {
                different.Add($"{QuoteLabels.ActionGroup}.{action}");
            }
        }

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Every_event_kind_has_a_text_that_matches_the_source()
    {
        var texts = Dictionary("tr");
        var prefix = QuoteLabels.EventGroup + ".";
        var keys = texts.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(keys);
        foreach (var en in new[] { "en", "az" })
        {
            var other = Dictionary(en);
            Assert.All(keys, k => Assert.True(other.ContainsKey(k), $"{en}: {k}"));
        }

        // Kaynak metin sözlüksüz çözülür; olay türü adı ("SubmittedForApproval") küçük harfli anahtarın karşılığıdır.
        var kinds = new[]
        {
            "Created", "Revised", "SubmittedForApproval", "ApprovalWithdrawn", "Approved", "ApprovalRejected", "ChangesRequested", "ApprovalInvalidated",
            "Presented", "Accepted", "Rejected", "Cancelled", "Superseded", "DocumentGenerated", "DeliveryUpdated", "Expired", "OpportunityWon",
            "OpportunityWinSkipped", "FollowUpScheduled", "FollowUpsClosed", "OpportunitySynced", "StepApproved", "ApprovalReminderSent",
            "ExpiryReminderCreated", "ShareLinkCreated", "ShareLinkRevoked", "CustomerViewed", "CustomerAccepted", "CustomerRejected",
        };
        Assert.Equal(kinds.Length, keys.Count);
        foreach (var kind in kinds)
        {
            Assert.Equal(texts[prefix + kind.ToLowerInvariant()], QuoteLabels.EventLabel(kind));
        }
    }

    [Theory]
    [InlineData(0, "1 saatten az")]
    [InlineData(5, "5 saat")]
    [InlineData(72, "3 gün")]
    public void Waiting_text_uses_hours_then_days_in_the_loaded_dictionary(int hours, string expected)
    {
        // Sözlük yüklenmediği için anahtar adı döner; biçim kuralı (saat/gün eşiği) anahtar seçiminden doğrulanır.
        var text = QuoteLabels.Waiting(TimeSpan.FromHours(hours));

        Assert.Contains(expected.Contains("saatten") ? "waitLessThanHour" : expected.Contains("saat") ? "waitHours" : "waitDays", text);
    }
}
