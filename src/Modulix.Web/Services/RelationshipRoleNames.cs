using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Kişi–firma ilişki rollerinin (<see cref="CrmRelationshipRoles"/>) etkin dildeki adları (ADR-0034). Paylaşılan sözlükteki
/// Türkçe ad kaynak metindir; anahtar yoksa o gösterilir. Anahtar: <c>crm.role.{rol}</c> (küçük harf).
/// </summary>
public static class RelationshipRoleNames
{
    public static string KeyOf(string role) => "crm.role." + role.ToLowerInvariant();

    public static string Of(string role) => Texts.GetOr(KeyOf(role), CrmRelationshipRoles.Labels.GetValueOrDefault(role, role));

    /// <summary>Bütün roller (anahtar, görünen ad), katalog sırasıyla.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All() =>
        CrmRelationshipRoles.Labels.Select(r => new KeyValuePair<string, string>(r.Key, Of(r.Key)));
}
