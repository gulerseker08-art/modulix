using Modulix.Shared.Access;
using Modulix.Shared.Users;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 16a); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Kaynak metinler paylaşılan sözleşmelerdeki (Shared) etiketlerden okunur (UserLabels, RecordScopeLabels, RoleAssignLevelLabels,
// BusinessUnitKindLabels); onlar sunucuda da kullanıldığı için Türkçe kalır. Tablolar özellik olarak tanımlıdır: metinler her okunuşta
// çözülür (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar eklenir (`AdminLabelsTests` kapsamı denetler).

/// <summary>Yönetim ekranlarının (kullanıcılar, roller, departmanlar) etiketleri.</summary>
public static class AdminLabels
{
    public const string UserStatusGroup = "admin.user-status";
    public const string ScopeGroup = "admin.scope";
    public const string ScopeDescriptionGroup = "admin.scope-description";
    public const string AssignLevelGroup = "admin.assign-level";
    public const string UnitKindGroup = "admin.unit-kind";

    public static string KeyOf(string group, string key) => $"{group}.{(key.StartsWith('-') ? "desc-" + key[1..] : key).ToLowerInvariant()}";

    public static string Of(string group, string key, string fallback) => Texts.GetOr(KeyOf(group, key), fallback);

    /// <summary>Tabloyu her okunuşta çevirir.</summary>
    public static IReadOnlyList<(string Key, string Label)> Translate(string group, IReadOnlyList<(string Key, string Label)> source) =>
        [.. source.Select(x => (x.Key, Of(group, x.Key, x.Label)))];

    /// <summary>Tablodaki karşılığı çevirir; tabloda olmayan anahtar <paramref name="otherwise"/> (verilmezse anahtarın kendisi) olarak döner.</summary>
    public static string Lookup(string group, IReadOnlyList<(string Key, string Label)> source, string key, string? otherwise = null)
    {
        foreach (var (k, label) in source)
        {
            if (k == key)
            {
                return Of(group, k, label);
            }
        }

        return otherwise ?? key;
    }

    internal static IReadOnlyList<(string Key, string Label)> UserStatusSource =>
        [.. Enum.GetValues<UserStatus>().Select(s => (s.ToString(), UserLabels.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> ScopeSource =>
        [.. Enum.GetValues<RecordScope>().Select(s => (s.ToString(), RecordScopeLabels.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> ScopeDescriptionSource =>
        [.. Enum.GetValues<RecordScope>().Select(s => (s.ToString(), RecordScopeLabels.Describe(s)))];

    internal static IReadOnlyList<(string Key, string Label)> AssignLevelSource =>
        [.. Enum.GetValues<RoleAssignLevel>().Select(l => (l.ToString(), RoleAssignLevelLabels.Of(l)))];

    internal static IReadOnlyList<(string Key, string Label)> UnitKindSource =>
        [.. BusinessUnitKindLabels.Labels.Select(l => (l.Key, l.Value))];

    public static IReadOnlyList<(string Key, string Label)> UserStatuses => Translate(UserStatusGroup, UserStatusSource);

    public static IReadOnlyList<(string Key, string Label)> Scopes => Translate(ScopeGroup, ScopeSource);

    public static IReadOnlyList<(string Key, string Label)> ScopeDescriptions => Translate(ScopeDescriptionGroup, ScopeDescriptionSource);

    public static IReadOnlyList<(string Key, string Label)> AssignLevels => Translate(AssignLevelGroup, AssignLevelSource);

    public static IReadOnlyList<(string Key, string Label)> UnitKinds => Translate(UnitKindGroup, UnitKindSource);

    public static string Status(UserStatus status) => Of(UserStatusGroup, status.ToString(), UserLabels.Of(status));

    public static string Scope(RecordScope scope) => Of(ScopeGroup, scope.ToString(), RecordScopeLabels.Of(scope));

    public static string ScopeDescription(RecordScope scope) => Of(ScopeDescriptionGroup, scope.ToString(), RecordScopeLabels.Describe(scope));

    public static string AssignLevel(RoleAssignLevel level) => Of(AssignLevelGroup, level.ToString(), RoleAssignLevelLabels.Of(level));

    public static string UnitKind(string kind) => Of(UnitKindGroup, kind, BusinessUnitKindLabels.Of(kind));
}
