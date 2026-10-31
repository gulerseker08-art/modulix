using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modulix.Modules.Contracts.Lifecycle;

// Sözleşme şablon motoru (ADR-0022, dilim 4). Şablon düz metindir: kod, ifade, döngü ve HTML YOKTUR. Yalnızca katalogdaki kontrollü değişkenler
// ({{musteri.unvan}}) ve tek başına satırda yazılan blok değişkenleri ({{kalem_tablosu}}) değiştirilir. Çıktı metin bloklarıdır; PDF çizimi host'tadır.
// Değişken değerleri metin olarak basılır, hiçbir zaman yorumlanmaz: değerin içindeki {{...}} ya da HTML etkisizdir (yeniden değiştirme yok).

/// <summary>Şablon durumu: taslak, yayımlanmış, arşivlenmiş.</summary>
public static class TemplateStatus
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Archived = "Archived";

    public static IReadOnlyList<string> All { get; } = [Draft, Published, Archived];
}

public static class TemplateLanguages
{
    public static IReadOnlyList<string> All { get; } = ["tr", "en"];
}

/// <summary>Şablonda kullanılabilen bir değişken. <see cref="Financial"/> değişken, finansal alan izni olmayan kullanıcıya belge/önizleme üretmez.</summary>
public sealed record TemplateVariable(string Key, string Label, string Group, bool Financial = false, bool IsBlock = false);

public static class ContractTemplateVariables
{
    public const string LineTable = "kalem_tablosu";
    public const string PartyList = "taraflar_listesi";
    public const string Signature = "imza_alani";
    public const string PageBreak = "sayfa_sonu";

    public static IReadOnlyList<TemplateVariable> All { get; } =
    [
        new("sozlesme.numara", "Sözleşme numarası", "Sözleşme"),
        new("sozlesme.baslik", "Sözleşme başlığı", "Sözleşme"),
        new("sozlesme.tur", "Sözleşme türü", "Sözleşme"),
        new("sozlesme.aciklama", "Açıklama", "Sözleşme"),
        new("sozlesme.surum", "Sürüm numarası", "Sözleşme"),
        new("sozlesme.baslangic_tarihi", "Başlangıç tarihi", "Süre"),
        new("sozlesme.bitis_tarihi", "Bitiş tarihi (belirsiz süreliyse \"Belirsiz süreli\")", "Süre"),
        new("sozlesme.yenileme_yontemi", "Yenileme yöntemi", "Süre"),
        new("sozlesme.yenileme_suresi_ay", "Yenileme süresi (ay)", "Süre"),
        new("sozlesme.bildirim_suresi_gun", "Bildirim süresi (gün)", "Süre"),
        new("sozlesme.son_bildirim_tarihi", "Son bildirim tarihi", "Süre"),
        new("sozlesme.para_birimi", "Para birimi", "Ticari"),
        new("sozlesme.odeme_kosullari", "Ödeme koşulları", "Ticari"),
        new("sozlesme.bedel", "Sözleşme bedeli", "Ticari", Financial: true),
        new("sozlesme.net_toplam", "Net toplam (tek seferlik)", "Ticari", Financial: true),
        new("sozlesme.vergi_toplam", "Vergi toplamı", "Ticari", Financial: true),
        new("sozlesme.genel_toplam", "Genel toplam", "Ticari", Financial: true),
        new("musteri.unvan", "Müşteri ünvanı", "Müşteri"),
        new("musteri.vergi_no", "Müşteri vergi / kimlik no", "Müşteri"),
        new("musteri.vergi_dairesi", "Müşteri vergi dairesi", "Müşteri"),
        new("musteri.adres", "Müşteri adresi", "Müşteri"),
        new("musteri.sehir", "Müşteri şehri", "Müşteri"),
        new("musteri.eposta", "Müşteri e-postası", "Müşteri"),
        new("musteri.telefon", "Müşteri telefonu", "Müşteri"),
        new("musteri.yetkili", "Müşteri yetkilisi (ad soyad)", "Müşteri"),
        new("hizmet_veren.unvan", "Hizmet veren ünvanı (firma ayarı)", "Hizmet veren"),
        new("tarih.bugun", "Belge üretim tarihi", "Belge"),
        new(LineTable, "Kalem tablosu (tutarlı)", "Bloklar", Financial: true, IsBlock: true),
        new(PartyList, "Taraflar ve ilgili kişiler listesi", "Bloklar", IsBlock: true),
        new(Signature, "İmza alanı (müşteri ve hizmet veren)", "Bloklar", IsBlock: true),
        new(PageBreak, "Sayfa sonu", "Bloklar", IsBlock: true),
    ];

    private static readonly Dictionary<string, TemplateVariable> ByKey = All.ToDictionary(v => v.Key, StringComparer.Ordinal);

    public static TemplateVariable? Find(string key) => ByKey.GetValueOrDefault(key);
}

public sealed record TemplateIssue(int Line, string Message);

/// <summary>Render edilen belge bloğu.</summary>
public abstract record DocBlock;

public sealed record HeadingBlock(int Level, string Text) : DocBlock;

public sealed record ParagraphBlock(string Text) : DocBlock;

public sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string> Totals) : DocBlock;

public sealed record PartyListBlock(IReadOnlyList<string> Items) : DocBlock;

public sealed record SignatureSlot(string Title, string Name);

public sealed record SignatureBlock(IReadOnlyList<SignatureSlot> Slots) : DocBlock;

public sealed record PageBreakBlock : DocBlock;

/// <summary>Belge verisi: metin değişken değerleri (biçimlendirilmiş) ve blok verileri. Boş değer "eksik" sayılır.</summary>
public sealed record ContractDocumentData(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<IReadOnlyList<string>> LineRows,
    IReadOnlyList<string> LineHeader,
    IReadOnlyList<string> LineTotals,
    IReadOnlyList<string> Parties,
    IReadOnlyList<SignatureSlot> Signers);

public sealed record TemplateRender(IReadOnlyList<DocBlock> Blocks, IReadOnlyList<string> Missing, IReadOnlyList<string> Used);

public static partial class ContractTemplateEngine
{
    public const int BodyMaxLength = 20000;

    [GeneratedRegex(@"\{\{(?<inner>[^{}]*)\}\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex KeyPattern();

    /// <summary>Sözdizimi ve değişken denetimi; sorun yoksa boş liste. Yayımlama ve kaydetme bunu çağırır.</summary>
    public static IReadOnlyList<TemplateIssue> Validate(string body)
    {
        var issues = new List<TemplateIssue>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return [new TemplateIssue(0, "Şablon metni boş olamaz.")];
        }

        if (body.Length > BodyMaxLength)
        {
            issues.Add(new TemplateIssue(0, $"Şablon metni en fazla {BodyMaxLength} karakter olabilir."));
        }

        var lines = Lines(body);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var number = i + 1;
            if (line.Contains("{%", StringComparison.Ordinal) || line.Contains("{#", StringComparison.Ordinal) || line.Contains("<%", StringComparison.Ordinal))
            {
                issues.Add(new TemplateIssue(number, "Şablon yönergeleri ve kod desteklenmez; yalnızca {{değişken}} kullanılabilir."));
            }

            var stripped = VariablePattern().Replace(line, string.Empty);
            if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal))
            {
                issues.Add(new TemplateIssue(number, "Kapanmamış ya da geçersiz {{ }} ifadesi."));
            }

            foreach (Match match in VariablePattern().Matches(line))
            {
                var key = match.Groups["inner"].Value.Trim();
                if (!KeyPattern().IsMatch(key))
                {
                    issues.Add(new TemplateIssue(number, $"Geçersiz değişken söz dizimi: {{{{{match.Groups["inner"].Value}}}}}. Yalnızca küçük harf, rakam, alt çizgi ve nokta kullanılır."));
                    continue;
                }

                var variable = ContractTemplateVariables.Find(key);
                if (variable is null)
                {
                    issues.Add(new TemplateIssue(number, $"Bilinmeyen değişken: {key}."));
                }
                else if (variable.IsBlock && !IsBlockLine(line, key))
                {
                    issues.Add(new TemplateIssue(number, $"Blok değişkeni ({key}) satırda tek başına yazılmalıdır."));
                }
            }
        }

        return issues;
    }

    /// <summary>Şablonda kullanılan değişkenler (tekrarsız, sırayla).</summary>
    public static IReadOnlyList<string> UsedVariables(string body) =>
        [.. VariablePattern().Matches(body).Select(m => m.Groups["inner"].Value.Trim()).Where(k => ContractTemplateVariables.Find(k) is not null).Distinct(StringComparer.Ordinal)];

    /// <summary>Kullanılan değişkenlerden finansal olan var mı? (izin denetimi)</summary>
    public static bool UsesFinancials(string body) =>
        UsedVariables(body).Any(k => ContractTemplateVariables.Find(k)!.Financial);

    /// <summary>
    /// Şablonu veriyle birleştirir. Değeri boş olan değişken ve verisi olmayan blok <see cref="TemplateRender.Missing"/> listesine girer
    /// (etiketiyle); üretim bu durumda reddedilir. Şablon geçersizse (Validate) istisna fırlatır: önce doğrulanmış olmalıdır.
    /// </summary>
    public static TemplateRender Render(string body, ContractDocumentData data)
    {
        if (Validate(body).Count > 0)
        {
            throw new InvalidOperationException("Şablon geçersiz; önce doğrulanmalıdır.");
        }

        var blocks = new List<DocBlock>();
        var missing = new List<string>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new ParagraphBlock(string.Join('\n', paragraph)));
                paragraph.Clear();
            }
        }

        string Substitute(string text) => VariablePattern().Replace(text, m =>
        {
            var key = m.Groups["inner"].Value.Trim();
            var value = data.Values.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!missing.Contains(Label(key)))
                {
                    missing.Add(Label(key));
                }

                return string.Empty;
            }

            return value;
        });

        foreach (var line in Lines(body))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (TryBlock(trimmed) is { } blockKey)
            {
                Flush();
                switch (blockKey)
                {
                    case ContractTemplateVariables.LineTable:
                        if (data.LineRows.Count == 0)
                        {
                            missing.Add("Kalem tablosu (sözleşmede kalem yok)");
                        }
                        else
                        {
                            blocks.Add(new TableBlock(data.LineHeader, data.LineRows, data.LineTotals));
                        }

                        break;
                    case ContractTemplateVariables.PartyList:
                        blocks.Add(new PartyListBlock(data.Parties));
                        break;
                    case ContractTemplateVariables.Signature:
                        blocks.Add(new SignatureBlock(data.Signers));
                        break;
                    case ContractTemplateVariables.PageBreak:
                        blocks.Add(new PageBreakBlock());
                        break;
                }

                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HeadingBlock(2, Substitute(trimmed[3..].Trim())));
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HeadingBlock(1, Substitute(trimmed[2..].Trim())));
            }
            else
            {
                paragraph.Add(Substitute(trimmed));
            }
        }

        Flush();
        return new TemplateRender(blocks, missing, UsedVariables(body));
    }

    /// <summary>Tutar biçimi (tr-TR: 1.234,50 TRY).</summary>
    public static string Money(decimal amount, string currency) => $"{amount.ToString("N2", Numbers)} {currency}";

    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    private static string Label(string key) => ContractTemplateVariables.Find(key)?.Label ?? key;

    private static List<string> Lines(string body) => [.. body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];

    private static bool IsBlockLine(string line, string key) => TryBlock(line.Trim()) == key;

    private static string? TryBlock(string trimmed)
    {
        var match = VariablePattern().Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length && ContractTemplateVariables.Find(match.Groups["inner"].Value.Trim()) is { IsBlock: true } variable
            ? variable.Key
            : null;
    }

    /// <summary>Dosya adı için güvenli kısa ad (Türkçe karakterler sadeleştirilir).</summary>
    public static string Slug(string text)
    {
        var map = new Dictionary<char, char> { ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u', ['Ç'] = 'c', ['Ğ'] = 'g', ['İ'] = 'i', ['I'] = 'i', ['Ö'] = 'o', ['Ş'] = 's', ['Ü'] = 'u' };
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            var mapped = map.TryGetValue(c, out var m) ? m : char.ToLowerInvariant(c);
            builder.Append(mapped is >= 'a' and <= 'z' or >= '0' and <= '9' ? mapped : '-');
        }

        return Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-') is { Length: > 0 } slug ? slug[..Math.Min(slug.Length, 40)] : "belge";
    }
}
