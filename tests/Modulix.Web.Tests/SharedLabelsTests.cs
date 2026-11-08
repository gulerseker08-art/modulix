using System.Text.Json;
using Modulix.Shared.Crm;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034: paylaşılan sözleşmelerdeki sabit etiket tablolarının (ilişki rolü, adres/iletişim/lokasyon/firma ilişkisi türü) çevirisi.
/// Tablodaki Türkçe ad kaynak metindir; her satır için <c>tr.json</c>'da anahtar vardır ve metin tabloyla aynıdır. Diller arası
/// anahtar eşitliğini mimari testler denetler.
/// </summary>
public class SharedLabelsTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void Every_table_row_has_a_turkish_text_equal_to_the_table_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string key, string label)
        {
            if (!tr.TryGetValue(key, out var text))
            {
                problems.Add($"eksik: {key} ({label})");
            }
            else if (text != label)
            {
                problems.Add($"farklı: {key} sözlük='{text}' tablo='{label}'");
            }
        }

        foreach (var (role, label) in CrmRelationshipRoles.Labels)
        {
            Check(RelationshipRoleNames.KeyOf(role), label);
        }

        foreach (var (group, labels) in new (string, IReadOnlyDictionary<string, string>)[]
                 {
                     (SharedLabels.AddressType, CrmAddressTypes.Labels),
                     (SharedLabels.ContactPointType, CrmContactPointTypes.Labels),
                     (SharedLabels.LocationType, CrmLocationTypes.Labels),
                     (SharedLabels.CompanyRelationType, CrmCompanyRelationTypes.Labels),
                 })
        {
            foreach (var (key, label) in labels)
            {
                Check($"{group}.{key.ToLowerInvariant()}", label);
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_table_label_is_shown()
    {
        Assert.Equal(CrmRelationshipRoles.Labels[CrmRelationshipRoles.Finance], RelationshipRoleNames.Of(CrmRelationshipRoles.Finance));
        Assert.Equal(CrmAddressTypes.Labels["Billing"], SharedLabels.Address("Billing"));
        Assert.Equal("Bilinmeyen", SharedLabels.ContactPoint("Bilinmeyen"));
    }
}
