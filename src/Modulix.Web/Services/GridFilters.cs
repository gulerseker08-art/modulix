namespace Modulix.Web.Services;

/// <summary>Kişisel görünümün süzgeç metni (<c>anahtar=değer&amp;anahtar=değer</c>, ADR-0036): sayfanın süzgeç durumuyla çevrilir.</summary>
public static class GridFilters
{
    /// <summary>Süzgeç metni → anahtar/değer (değerler çözülmüş).</summary>
    public static Dictionary<string, string> Parse(string? filters) =>
        (filters ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1]));

    /// <summary>Boş olmayan çiftlerden süzgeç metni; hiç yoksa null.</summary>
    public static string? Build(params (string Key, string Value)?[] pairs)
    {
        var parts = pairs.Where(p => p is { Value.Length: > 0 }).Select(p => $"{p!.Value.Key}={Uri.EscapeDataString(p.Value.Value)}").ToList();
        return parts.Count == 0 ? null : string.Join('&', parts);
    }
}
