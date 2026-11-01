using System.Text;

namespace Modulix.Modules.Support.Lifecycle;

// Ek, gelen mesaj ve anket kuralları (saf sınıflar; birim testle sabit girdiyle sınanır).

public static class AttachmentScan
{
    public const string NotScanned = "NotScanned";
}

/// <summary>Ek doğrulaması. Tür istemcinin bildirdiği içerik türüne değil, uzantı + dosya imzasına bakılarak belirlenir.</summary>
public static class AttachmentRules
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerMessage = 5;
    public const int MaxPerTicket = 20;

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
        if (result.Length > Domain.TicketAttachment.FileNameMaxLength)
        {
            var ext = Path.GetExtension(result);
            result = result[..(Domain.TicketAttachment.FileNameMaxLength - ext.Length)] + ext;
        }

        return result.Length == 0 ? null : result;
    }

    /// <summary>Adı, boyutu ve içeriği doğrular; geçerliyse güvenli ad ve türü döner.</summary>
    public static (string? FileName, string? ContentType, string? Error) Validate(string? fileName, ReadOnlySpan<byte> content)
    {
        var name = SafeName(fileName);
        if (name is null)
        {
            return (null, null, "Dosya adı zorunludur.");
        }

        if (content.Length == 0)
        {
            return (null, null, "Dosya boş olamaz.");
        }

        if (content.Length > MaxBytes)
        {
            return (null, null, $"Dosya en fazla {MaxBytes / (1024 * 1024)} MB olabilir.");
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
            if (content.Length < signature.Length || !content[..signature.Length].SequenceEqual(signature))
            {
                return (null, null, "Dosya içeriği uzantısıyla uyuşmuyor.");
            }
        }
        else if (content.Contains((byte)0))
        {
            // Metin dosyası ikili bayt içeremez.
            return (null, null, "Metin dosyası ikili içerik barındıramaz.");
        }

        return (name, rule.ContentType, null);
    }
}

/// <summary>Gelen kanal mesajı (e-posta, web formu, telefon kaydı...). Alanlar kanal adaptörünün normalize ettiği değerlerdir.</summary>
public sealed record InboundMessage(
    string Channel,
    string ExternalMessageId,
    string? InReplyTo,
    IReadOnlyList<string>? References,
    string? FromAddress,
    string? FromName,
    string? Subject,
    string? Body,
    string? AutoSubmitted,
    string? Precedence,
    bool SuppressAutoResponse,
    bool NullReturnPath);

/// <summary>Gelen mesajın nasıl işlendiği.</summary>
public static class InboundOutcome
{
    /// <summary>Var olan taleple eşleşti, mesaj olarak eklendi.</summary>
    public const string Appended = "Appended";

    /// <summary>Yeni talep açıldı.</summary>
    public const string Created = "Created";

    /// <summary>Aynı dış mesaj kimliği daha önce işlenmişti; hiçbir şey yazılmadı.</summary>
    public const string Duplicate = "Duplicate";

    /// <summary>Otomatik yanıt ya da geri dönen (bounce) mesaj; döngü koruması gereği talep açılmadı ve talebe eklenmedi.</summary>
    public const string IgnoredAutomatic = "IgnoredAutomatic";
}

/// <summary>Gelen mesaj kuralları: otomatik yanıt sınıflandırması, konu temizliği, talep numarası ayıklama.</summary>
public static class InboundRules
{
    public const int MaxMessageIdLength = 300;
    public const int MaxReferences = 20;

    /// <summary>RFC 3834 ve yaygın başlıklarla otomatik yanıt/bounce tespiti. Şüphede otomatik sayılır (döngüyü kesmek, bir mesajı atlamaktan ucuzdur).</summary>
    public static bool IsAutomatic(InboundMessage message)
    {
        if (message.NullReturnPath || message.SuppressAutoResponse)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(message.AutoSubmitted) && !message.AutoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(message.Precedence)
            && message.Precedence.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply")
        {
            return true;
        }

        var from = message.FromAddress?.Trim().ToLowerInvariant() ?? string.Empty;
        return from.StartsWith("mailer-daemon@", StringComparison.Ordinal) || from.StartsWith("postmaster@", StringComparison.Ordinal)
            || from.StartsWith("no-reply@", StringComparison.Ordinal) || from.StartsWith("noreply@", StringComparison.Ordinal);
    }

    /// <summary>"Re:", "RE:", "Ynt:", "Fwd:" gibi ön ekleri ve talep numarası etiketini atar.</summary>
    public static string CleanSubject(string? subject)
    {
        var text = (subject ?? string.Empty).Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in new[] { "re:", "ynt:", "fw:", "fwd:", "cevap:" })
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        var open = text.IndexOf("[TK-", StringComparison.Ordinal);
        if (open >= 0 && text.IndexOf(']', open) is var close and > 0)
        {
            text = (text[..open] + text[(close + 1)..]).Trim();
        }

        return text;
    }

    /// <summary>Konudaki köşeli parantezli talep numarasını ("[TK-20261101-AB12CD]") döner; yoksa null.</summary>
    public static string? TicketNumberFromSubject(string? subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var open = subject.IndexOf("[TK-", StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var close = subject.IndexOf(']', open);
        return close < 0 ? null : subject[(open + 1)..close].Trim();
    }

    /// <summary>İlişkili mesaj kimlikleri: In-Reply-To önce, sonra References (en yeniden eskiye), tekrarsız ve sınırlı.</summary>
    public static IReadOnlyList<string> ThreadIds(InboundMessage message)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            ids.Add(message.InReplyTo.Trim());
        }

        if (message.References is { } references)
        {
            ids.AddRange(references.Reverse().Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()));
        }

        return [.. ids.Distinct(StringComparer.Ordinal).Take(MaxReferences)];
    }
}

/// <summary>Memnuniyet anketi kuralları.</summary>
public static class SurveyRules
{
    public const int DefaultValidDays = 14;
    public const int MinRating = 1;
    public const int MaxRating = 5;

    /// <summary>Düşük puan: takip gerektirir (ayar yoksa 2 ve altı).</summary>
    public const int LowRatingThreshold = 2;

    public static bool IsValidRating(int rating) => rating is >= MinRating and <= MaxRating;

    public static bool IsLow(int rating) => rating <= LowRatingThreshold;

    /// <summary>Anket yanıtlanabilir mi: yanıtlanmamış ve süresi dolmamış.</summary>
    public static (bool Open, string? Reason) CanAnswer(DateTimeOffset? answeredAt, DateTimeOffset expiresAt, DateTimeOffset now) =>
        answeredAt is not null ? (false, "Bu anket daha önce yanıtlandı.")
        : expiresAt <= now ? (false, "Bu anketin süresi doldu.")
        : (true, null);
}
