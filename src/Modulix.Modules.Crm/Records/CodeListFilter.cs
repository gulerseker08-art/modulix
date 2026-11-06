namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Kod listesi alanı süzgeci (ADR-0029): liste "a,b" biçiminde saklanır ve tam kod eşleşir (kısmi eşleşme olmaz).
/// Kod geçersizse süzgeç sayılmaz (<c>null</c>); geçerli kod için bu sınıf yalnızca doğrulama yapar, sorgu çağıranda kurulur.
/// </summary>
internal static class CodeListFilter
{
    /// <summary>Faaliyet/rol kodu: küçük harf, rakam ve tire (en çok 50); başka değer süzgeç sayılmaz.</summary>
    public static string? Code(string value) =>
        value.Trim() is { Length: > 0 and <= 50 } code && code.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') ? code : null;
}
