using System.Globalization;
using System.Text;

namespace Modulix.Web.Services;

/// <summary>Genel aramada önerilen ekran: menüdeki adı, route'u, ikonu ve ait olduğu uygulama.</summary>
public sealed record ScreenLink(string Name, string Href, string Icon, string? Area = null);

/// <summary>
/// Genel aramanın "Ekranlar" grubu: kullanıcının menüsünde görünen ekranlar adına göre süzülür. Liste zaten
/// <see cref="AppNavigation"/> ile oturumdan hesaplanmıştır; burada yalnızca metin eşleştirilir (yetki route'ta ve sunucuda denetlenir).
/// </summary>
public static class ScreenSearch
{
    /// <summary>
    /// Adı sorguyla eşleşen ekranlar: önce adı sorguyla başlayanlar, sonra bir sözcüğü sorguyla başlayanlar, sonra içerenler.
    /// Büyük/küçük harf ve Türkçe işaretler yok sayılır ("firsat" → "Fırsatlar"); aynı route bir kez döner.
    /// </summary>
    public static IReadOnlyList<ScreenLink> Find(IEnumerable<ScreenLink> screens, string query, int limit)
    {
        string needle = Fold(query);
        if (needle.Length == 0 || limit <= 0)
        {
            return [];
        }

        return screens
            .Select((screen, index) => (screen, index, rank: Rank(Fold(screen.Name), needle)))
            .Where(m => m.rank >= 0)
            .OrderBy(m => m.rank)
            .ThenBy(m => m.index)
            .Select(m => m.screen)
            .DistinctBy(s => s.Href)
            .Take(limit)
            .ToList();
    }

    private static int Rank(string name, string needle)
    {
        if (name.StartsWith(needle, StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.Contains(" " + needle, StringComparison.Ordinal))
        {
            return 1;
        }

        return name.Contains(needle, StringComparison.Ordinal) ? 2 : -1;
    }

    /// <summary>Karşılaştırma biçimi: küçük harf, işaretsiz (ı→i, ş→s, ö→o…), baştaki ve sondaki boşluk atılmış.</summary>
    internal static string Fold(string text)
    {
        StringBuilder folded = new(text.Length);
        foreach (char c in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(c is 'ı' or 'I' or 'İ' ? 'i' : char.ToLowerInvariant(c));
        }

        return folded.ToString();
    }
}
