using System.Globalization;
using System.Text;
using System.Text.Json;
using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>
/// Rapor sonucunu CSV'ye çevirir (ADR-0035, dilim 4). Dışa aktarma ekrandaki sonuçla <b>aynı</b> <see cref="ReportResult"/>'tan üretilir; ayrı bir
/// hesap yoktur. CSV/Excel formül enjeksiyonuna karşı metin hücreleri ve başlıklar kaçışlanır: "=", "+", "-", "@", sekme ya da satır başı ile
/// başlayan metin başına kesme işareti konur (sayısal hücreler metin değildir ve olduğu gibi yazılır). UTF-8 BOM'lu, CRLF satır sonlu, noktalı
/// virgüllü değil virgüllü CSV; sayılar değişmez kültürle (nokta ondalık).
/// </summary>
public static class ReportCsv
{
    private static readonly char[] DangerousStarts = ['=', '+', '-', '@', '\t', '\r', '\n'];

    public static byte[] Build(ReportResult result)
    {
        var builder = new StringBuilder();
        var money = !result.IsSummary && result.Rows.Any(r => r.Currency is not null);
        var header = result.Columns.Select(c => c.Label).ToList();
        if (money)
        {
            header.Add("Para birimi");
        }

        AppendLine(builder, header.Select(Text));
        foreach (var row in result.Rows)
        {
            var cells = row.Cells.Select(Cell).ToList();
            if (money)
            {
                cells.Add(Text(row.Currency));
            }

            AppendLine(builder, cells);
        }

        // Toplam satırları özet raporda sütunlarla aynı hizadadır; ilk grup hücresine "Toplam" yazılır. Tablo raporunda toplam satırı yoktur.
        foreach (var total in result.IsSummary ? result.Totals : [])
        {
            var cells = total.Cells.Select(Cell).ToList();
            if (cells.Count > 0 && result.Columns[0].Role == ReportColumnRole.Group)
            {
                cells[0] = "Toplam";
            }

            AppendLine(builder, cells);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    /// <summary>Dosya adı için güvenli kısa metin (harf, rakam, tire); boşsa "rapor".</summary>
    public static string FileSlug(string? name)
    {
        var slug = new StringBuilder();
        foreach (var c in Transliterate((name ?? string.Empty).Trim()).Normalize(NormalizationForm.FormKD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (slug.Length > 0 && slug[^1] != '-' && (char.IsWhiteSpace(c) || c is '-' or '_' or '.'))
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        return text.Length == 0 ? "rapor" : text[..Math.Min(text.Length, 60)];
    }

    /// <summary>Türkçe harfler dosya adında düşmesin diye (açık → acik) ASCII karşılıklarına çevrilir; NFKD ı, İ ve ş'yi ayrıştırmaz.</summary>
    private static string Transliterate(string text) => text
        .Replace('ç', 'c').Replace('Ç', 'C').Replace('ğ', 'g').Replace('Ğ', 'G').Replace('ı', 'i').Replace('İ', 'I')
        .Replace('ö', 'o').Replace('Ö', 'O').Replace('ş', 's').Replace('Ş', 'S').Replace('ü', 'u').Replace('Ü', 'U');

    private static string Cell(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
        JsonElement { ValueKind: JsonValueKind.Number } number => number.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.True } => "Evet",
        JsonElement { ValueKind: JsonValueKind.False } => "Hayır",
        JsonElement element => Text(element.ToString()),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Evet" : "Hayır",
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>Metin hücresi: formül başlangıcı kaçışlanır. Kaçışlanan değer bir sayı gibi görünse bile metin olarak kalır.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return DangerousStarts.Contains(value[0]) ? "'" + value : value;
    }

    /// <summary>
    /// <see cref="Text"/>'in tersi: başına kaçış için kesme işareti konmuş hücrenin asıl değeri. Hata raporu düzeltilip yeniden yüklendiğinde
    /// "+90…" gibi değerler bozulmasın diye kullanılır; kesme işaretiyle başlayan başka metne dokunulmaz.
    /// </summary>
    public static string Unescape(string value) =>
        value.Length > 1 && value[0] == '\'' && DangerousStarts.Contains(value[1]) ? value[1..] : value;

    private static void AppendLine(StringBuilder builder, IEnumerable<string> cells)
    {
        builder.Append(string.Join(',', cells.Select(c => Quote(c, ',')))).Append("\r\n");
    }

    /// <summary>Hücreyi verilen ayraca göre tırnaklar (RFC 4180).</summary>
    public static string Quote(string value, char delimiter) =>
        value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
