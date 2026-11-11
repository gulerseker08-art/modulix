using System.Text;

namespace Modulix.Core.Files;

/// <summary>
/// Yükleme doğrulaması (ADR-0040 K3). Tür, istemcinin bildirdiği içerik türüne değil uzantı + dosya imzasına bakılarak belirlenir.
/// Veritabanı bilmez; birim testle sabit girdiyle sınanır.
/// </summary>
public static class FileRules
{
    public const int MaxPerRecord = 20;

    private static readonly Dictionary<string, (string ContentType, byte[]? Signature)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = ("application/pdf", "%PDF"u8.ToArray()),
        [".png"] = ("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        [".jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".gif"] = ("image/gif", "GIF8"u8.ToArray()),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]),
        [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [0x50, 0x4B, 0x03, 0x04]),
        [".doc"] = ("application/msword", [0xD0, 0xCF, 0x11, 0xE0]),
        [".xls"] = ("application/vnd.ms-excel", [0xD0, 0xCF, 0x11, 0xE0]),
        [".txt"] = ("text/plain", null),
        [".csv"] = ("text/csv", null),
    };

    /// <summary>Dosya adının ortasında bulunursa ("fatura.exe.pdf") reddedilen uzantılar.</summary>
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".js", ".vbs", ".ps1", ".msi", ".jar", ".sh", ".html", ".htm", ".svg", ".hta", ".lnk",
    };

    public static IReadOnlyCollection<string> AllowedExtensions => Allowed.Keys;

    /// <summary>İmzayı doğrulamak için içeriğin başından okunması gereken bayt sayısı.</summary>
    public const int SignatureProbeBytes = 8;

    /// <summary>Güvenli dosya adı: yol parçaları, denetim karakterleri ve ayırıcılar atılır, uzunluk sınırlanır. Boş kalırsa null.</summary>
    public static string? SafeName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            cleaned.Append(char.IsControl(c) || c is '"' or '<' or '>' or ':' or '*' or '?' or '|' ? '_' : c);
        }

        var result = cleaned.ToString().Trim().TrimEnd('.');
        if (result.Length > StoredFile.FileNameMaxLength)
        {
            var ext = Path.GetExtension(result);
            result = result[..(StoredFile.FileNameMaxLength - ext.Length)] + ext;
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Adı, boyutu ve içeriğin başını doğrular; geçerliyse güvenli ad ve türü döner. <paramref name="head"/> içeriğin ilk baytlarıdır
    /// (en az <see cref="SignatureProbeBytes"/>, içerik daha kısaysa tamamı); <paramref name="length"/> tüm içeriğin uzunluğudur.
    /// Metin dosyalarında ikili bayt denetimi başlık üzerinde yapılır; tam içerik denetimi dilim 2'nin (tarama) işidir.
    /// </summary>
    public static (string? FileName, string? ContentType, string? Error) Validate(string? fileName, ReadOnlySpan<byte> head, long length, long maxBytes)
    {
        var name = SafeName(fileName);
        if (name is null)
        {
            return (null, null, "Dosya adı zorunludur.");
        }

        if (length == 0)
        {
            return (null, null, "Dosya boş olamaz.");
        }

        if (length > maxBytes)
        {
            return (null, null, $"Dosya en fazla {FormatSize(maxBytes)} olabilir.");
        }

        var extension = Path.GetExtension(name);
        if (!Allowed.TryGetValue(extension, out var rule))
        {
            return (null, null, $"Bu dosya türüne izin verilmez. İzinli türler: {string.Join(", ", Allowed.Keys.Order())}.");
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (Dangerous.Any(d => stem.Contains(d, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, null, "Dosya adı çalıştırılabilir ya da betik uzantısı içeremez.");
        }

        if (rule.Signature is { } signature)
        {
            if (head.Length < signature.Length || !head[..signature.Length].SequenceEqual(signature))
            {
                return (null, null, "Dosya içeriği uzantısıyla uyuşmuyor.");
            }
        }
        else if (head.Contains((byte)0))
        {
            return (null, null, "Metin dosyası ikili içerik barındıramaz.");
        }

        return (name, rule.ContentType, null);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";
}
