using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Shared.Credentials;
using Modulix.Shared.Fleet;
using Modulix.Shared.Records;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 aşama 4, dilim 17a (Filo ve Belgeler): mülkiyet, araç durumu ve belge durumu seçenekleri sözlükten gelir; Türkçe kaynak metin
/// (koddaki yedek) <c>tr.json</c> ile aynıdır. Belge durumu kararı metne bakmaz (<see cref="CredentialLabels.StateOf"/>).
/// </summary>
public class FleetTextTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))!;
    }

    [Fact]
    public void Option_lists_have_a_turkish_text_equal_to_the_source_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string group, IReadOnlyList<(string Key, string Label)> list)
        {
            foreach (var (key, label) in list)
            {
                var name = $"{group}.{key.ToLowerInvariant()}";
                if (!tr.TryGetValue(name, out var text))
                {
                    problems.Add($"eksik: {name} ({label})");
                }
                else if (text != label)
                {
                    problems.Add($"farklı: {name} sözlük='{text}' kaynak='{label}'");
                }
            }
        }

        Check("fleet.ownership", FleetAccess.Ownerships);
        Check("fleet.status", FleetAccess.Statuses);
        Check("credentials.status", CredentialAccess.Statuses);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_turkish_source_label_is_shown()
    {
        Assert.Equal("Özmal", FleetLabels.Ownership("owned"));
        Assert.Equal("Bakımda", FleetLabels.Status("maintenance"));
        Assert.Equal("İptal edildi", CredentialLabels.Status(CredentialAccess.Revoked));
        Assert.Equal("bilinmeyen", FleetLabels.Status("bilinmeyen"));
    }

    private static RecordResponse Credential(string? status = null, string? validFrom = null, string? validTo = null)
    {
        var values = new JsonObject { ["status"] = status ?? CredentialAccess.Active };
        if (validFrom is not null)
        {
            values["validFrom"] = validFrom;
        }

        if (validTo is not null)
        {
            values["validTo"] = validTo;
        }

        return new RecordResponse(CredentialAccess.EntityKey, Guid.NewGuid(), 1, values);
    }

    [Fact]
    public void Credential_state_does_not_depend_on_the_displayed_text()
    {
        var today = new DateOnly(2026, 11, 14);
        string Day(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");

        Assert.Equal(CredentialState.Revoked, CredentialLabels.StateOf(Credential(CredentialAccess.Revoked, validTo: Day(-5)), today));
        Assert.Equal(CredentialState.Expired, CredentialLabels.StateOf(Credential(validTo: Day(-1)), today));
        Assert.Equal(CredentialState.NotStarted, CredentialLabels.StateOf(Credential(validFrom: Day(3), validTo: Day(400)), today));
        Assert.Equal(CredentialState.DueToday, CredentialLabels.StateOf(Credential(validTo: Day(0)), today));
        Assert.Equal(CredentialState.Expiring, CredentialLabels.StateOf(Credential(validTo: Day(CredentialAccess.ExpiringDays)), today));
        Assert.Equal(CredentialState.Valid, CredentialLabels.StateOf(Credential(validTo: Day(CredentialAccess.ExpiringDays + 1)), today));
        Assert.Equal(CredentialState.Valid, CredentialLabels.StateOf(Credential(), today));
    }
}
