using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Paylaşılan sözleşmelerdeki (Shared) sabit etiket tablolarının etkin dildeki karşılıkları (ADR-0034). Tablodaki Türkçe ad
/// kaynak metindir; sözlükte <c>{grup}.{anahtar küçük harf}</c> yoksa o gösterilir. Gruplar aşağıdaki sabitlerdir.
/// Tabloya satır eklenince üç sözlüğe de anahtar eklenir (<c>SharedLabelsTests</c> kapsamı denetler).
/// </summary>
public static class SharedLabels
{
    public const string LocationType = "crm.location-type";
    public const string AddressType = "crm.address-type";
    public const string ContactPointType = "crm.contact-point-type";
    public const string CompanyRelationType = "crm.relation-type";

    public static string Of(string group, string key, IReadOnlyDictionary<string, string> labels) =>
        Texts.GetOr($"{group}.{key.ToLowerInvariant()}", labels.GetValueOrDefault(key, key));

    /// <summary>Tablonun bütün satırları (anahtar, görünen ad), tablo sırasıyla.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All(string group, IReadOnlyDictionary<string, string> labels) =>
        labels.Select(l => new KeyValuePair<string, string>(l.Key, Of(group, l.Key, labels)));

    public static IEnumerable<KeyValuePair<string, string>> Addresses() => All(AddressType, CrmAddressTypes.Labels);

    public static string Address(string key) => Of(AddressType, key, CrmAddressTypes.Labels);

    public static IEnumerable<KeyValuePair<string, string>> ContactPoints() => All(ContactPointType, CrmContactPointTypes.Labels);

    public static string ContactPoint(string key) => Of(ContactPointType, key, CrmContactPointTypes.Labels);
}
