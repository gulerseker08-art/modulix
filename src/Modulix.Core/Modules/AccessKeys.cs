using System.Text.RegularExpressions;

namespace Modulix.Core.Modules;

/// <summary>Modül ve izin anahtarlarının biçim kuralları.</summary>
public static partial class AccessKeys
{
    public const int ModuleKeyMaxLength = 30;
    public const int PermissionKeyMaxLength = 100;

    /// <summary>Küçük harfle başlar; a-z ve 0-9; 2-30 karakter (ör. "crm", "logistics").</summary>
    public static bool IsValidModuleKey(string? key) =>
        key is { Length: <= ModuleKeyMaxLength } && ModuleKeyPattern().IsMatch(key);

    /// <summary>"modül.kaynak.işlem" biçiminde, en az üç bölüm (ör. "crm.companies.read").</summary>
    public static bool IsValidPermissionKey(string? key) =>
        key is { Length: <= PermissionKeyMaxLength } && PermissionKeyPattern().IsMatch(key);

    public const int EntityKeyMaxLength = 100;

    /// <summary>Kayıt türü anahtarı: "modül.tür" biçiminde (ör. "crm.opportunity", "crm.company-contact"); en çok 100 karakter.</summary>
    public static bool IsValidEntityKey(string? key) =>
        key is { Length: <= EntityKeyMaxLength } && EntityKeyPattern().IsMatch(key);

    /// <summary>İzin anahtarının ait olduğu modül (ilk bölüm).</summary>
    public static string ModuleOf(string permissionKey)
    {
        var dot = permissionKey.IndexOf('.');
        return dot > 0 ? permissionKey[..dot] : permissionKey;
    }

    /// <summary>İzin anahtarının kaynağı: modül ile işlem arasındaki bölüm (ör. "crm.companies.read" → "companies").</summary>
    public static string ResourceOf(string permissionKey)
    {
        var first = permissionKey.IndexOf('.');
        var last = permissionKey.LastIndexOf('.');
        return first > 0 && last > first ? permissionKey[(first + 1)..last] : permissionKey;
    }

    [GeneratedRegex("^[a-z][a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9]+(?:\\.[a-z][a-z0-9-]*){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9]+\\.[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityKeyPattern();
}
