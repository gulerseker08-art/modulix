using System.Security.Cryptography;
using System.Text;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>İmza isteğinin durumu (<c>ContractSignatureRequests.Status</c>).</summary>
public static class SignatureRequestStatus
{
    public const string Pending = "Pending";
    public const string PartiallySigned = "PartiallySigned";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Pending, PartiallySigned, Completed, Rejected, Expired, Cancelled];

    /// <summary>Hâlâ imza bekleyen istek.</summary>
    public static bool IsOpen(string status) => status is Pending or PartiallySigned;
}

/// <summary>İmzacının durumu.</summary>
public static class SignatoryStatus
{
    /// <summary>Sıralı imzada sırası gelmedi.</summary>
    public const string Waiting = "Waiting";
    public const string Sent = "Sent";
    public const string Viewed = "Viewed";
    public const string Signed = "Signed";
    public const string Declined = "Declined";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    public static bool IsOpen(string status) => status is Waiting or Sent or Viewed;
}

public static class SignatoryKind
{
    public const string Internal = "Internal";
    public const string External = "External";

    public static IReadOnlyList<string> All { get; } = [Internal, External];
}

public static class SignatureMode
{
    public const string Sequential = "Sequential";
    public const string Parallel = "Parallel";

    public static IReadOnlyList<string> All { get; } = [Sequential, Parallel];
}

/// <summary>
/// İmza isteğinin yöntemi. <see cref="Manual"/>: imzalı belge (ıslak imza taraması) yüklenir ve yetkili kişi doğrular. <see cref="Provider"/>: harici imza
/// sağlayıcısı (ya da simülasyonu) imzayı toplar ve olay (webhook) ile bildirir. İki yöntem eşdeğer gösterilmez: güvence düzeyi ayrı saklanır.
/// </summary>
public static class SignatureMethod
{
    public const string Manual = "Manual";
    public const string Provider = "Provider";

    public static IReadOnlyList<string> All { get; } = [Manual, Provider];
}

/// <summary>
/// Güvence düzeyi: imzanın nasıl kanıtlandığı. Düzeyler birbirine eşit sayılmaz ve hukuki geçerlilik iddiası taşımaz.
/// <see cref="ManualScan"/>: imzalı belge taraması, yetkili kişi tarafından doğrulandı (kimlik/yetki doğrulaması Modulix dışındadır).
/// <see cref="SimulatedElectronic"/>: yalnızca test simülasyonu; hiçbir hukuki değeri yoktur.
/// <see cref="ProviderElectronic"/> / <see cref="ProviderQualified"/>: gerçek bir sağlayıcı bu düzeyi bildirdi; Modulix doğrulayamaz, sağlayıcıya güvenir.
/// </summary>
public static class SignatureAssurance
{
    public const string ManualScan = "ManualScan";
    public const string SimulatedElectronic = "SimulatedElectronic";
    public const string ProviderElectronic = "ProviderElectronic";
    public const string ProviderQualified = "ProviderQualified";

    public static IReadOnlyList<string> All { get; } = [ManualScan, SimulatedElectronic, ProviderElectronic, ProviderQualified];
}

public static class SignatureEventType
{
    public const string Requested = "Requested";
    public const string Sent = "Sent";
    public const string Viewed = "Viewed";
    public const string Signed = "Signed";
    public const string Declined = "Declined";
    public const string Reminded = "Reminded";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";

    /// <summary>Sağlayıcının bildirebileceği olaylar.</summary>
    public static IReadOnlyList<string> FromProvider { get; } = [Sent, Viewed, Signed, Declined, Expired];
}

/// <summary>Olayın işlenme sonucu (olay kaydının <c>Outcome</c> alanı): tekrarlanan ya da sırası bozuk olay kaydedilir ama uygulanmaz.</summary>
public static class SignatureEventOutcome
{
    public const string Applied = "Applied";
    public const string Duplicate = "Duplicate";
    public const string OutOfOrder = "OutOfOrder";
    public const string Rejected = "Rejected";
    public const string Ignored = "Ignored";
}

/// <summary>Değerlendirmeye giren imzacı (saf; veritabanını bilmez).</summary>
public sealed record SignatoryInfo(Guid Id, int Position, bool IsRequired, string Status);

/// <summary>
/// İmza akışı kuralları (saf): sıra, tamamlanma ve istek durumu. Zorunlu imzacıların hepsi imzalayınca istek tamamlanır; zorunlu bir imzacı reddederse
/// reddedilir; isteğe bağlı imzacının reddi isteği bozmaz. Sıralı kipte bir imzacı yalnızca kendinden önceki zorunlu imzacılar imzaladıktan sonra imzalar.
/// </summary>
public static class SignatureFlow
{
    /// <summary>İstek durumu: hiç imza yoksa bekliyor, bir kısmı varsa kısmen imzalandı, zorunluların hepsi tamamsa tamamlandı, zorunlu ret varsa reddedildi.</summary>
    public static string Evaluate(IReadOnlyList<SignatoryInfo> signatories)
    {
        if (signatories.Any(s => s.IsRequired && s.Status == SignatoryStatus.Declined))
        {
            return SignatureRequestStatus.Rejected;
        }

        var required = signatories.Where(s => s.IsRequired).ToList();
        if (required.Count > 0 && required.All(s => s.Status == SignatoryStatus.Signed))
        {
            return SignatureRequestStatus.Completed;
        }

        return signatories.Any(s => s.Status == SignatoryStatus.Signed) ? SignatureRequestStatus.PartiallySigned : SignatureRequestStatus.Pending;
    }

    /// <summary>İmzacı şimdi imzalayabilir mi? Açık değilse nedeni; sıralı kipte önceki zorunlu imzacılar tamamlanmış olmalıdır.</summary>
    public static (bool Allowed, string? Reason) CanSign(string mode, IReadOnlyList<SignatoryInfo> signatories, Guid signatoryId)
    {
        var current = signatories.FirstOrDefault(s => s.Id == signatoryId);
        if (current is null)
        {
            return (false, "İmzacı bulunamadı.");
        }

        if (!SignatoryStatus.IsOpen(current.Status))
        {
            return (false, current.Status == SignatoryStatus.Signed ? "Bu imzacı zaten imzaladı." : "Bu imzacı için imza artık alınamaz.");
        }

        if (mode == SignatureMode.Sequential && signatories.Any(s => s.Position < current.Position && s.IsRequired && s.Status != SignatoryStatus.Signed))
        {
            return (false, "Sıralı imzada önce önceki zorunlu imzacılar imzalamalıdır.");
        }

        return (true, null);
    }

    /// <summary>Sıralı kipte şu an imza sırası gelen imzacılar (önceki zorunlu imzacılar tamamlanmış açık imzacılar); paralel kipte bütün açık imzacılar.</summary>
    public static IReadOnlyList<Guid> Active(string mode, IReadOnlyList<SignatoryInfo> signatories) =>
        [.. signatories.Where(s => SignatoryStatus.IsOpen(s.Status) && CanSign(mode, signatories, s.Id).Allowed).Select(s => s.Id)];

    /// <summary>Sağlayıcı olayının sırası: olay numarası son uygulananın üstünde olmalıdır. Yinelenen olay kimliği ayrıca <see cref="SignatureEventOutcome.Duplicate"/> sayılır.</summary>
    public static string ClassifyProviderEvent(bool eventIdSeen, int lastSequence, int sequence)
    {
        if (eventIdSeen)
        {
            return SignatureEventOutcome.Duplicate;
        }

        return sequence > lastSequence ? SignatureEventOutcome.Applied : SignatureEventOutcome.OutOfOrder;
    }
}

/// <summary>İmzalı belge dosyası doğrulaması (saf): tür, boyut ve dosya imzası (içerik türüne güvenilmez, ilk baytlara bakılır).</summary>
public static class SignatureFileRules
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static string? Validate(string? fileName, byte[] content, out string contentType)
    {
        contentType = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Dosya adı zorunludur.";
        }

        if (content.Length == 0)
        {
            return "Dosya boş.";
        }

        if (content.Length > MaxBytes)
        {
            return "Dosya en fazla 10 MB olabilir.";
        }

        if (content.AsSpan().StartsWith("%PDF-"u8))
        {
            contentType = "application/pdf";
        }
        else if (content.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            contentType = "image/png";
        }
        else if (content.Length > 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            contentType = "image/jpeg";
        }
        else
        {
            return "Yalnızca PDF, PNG ya da JPEG dosyası yüklenebilir.";
        }

        return null;
    }

    /// <summary>İndirme için güvenli dosya adı: yol ve denetim karakterleri atılır, uzunluk sınırlanır.</summary>
    public static string SafeName(string fileName, int max = 120)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var clean = new string([.. name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?'))]).Trim();
        if (clean.Length == 0)
        {
            clean = "imzali-belge";
        }

        return clean.Length > max ? clean[..max] : clean;
    }
}

/// <summary>
/// Sağlayıcı webhook imzası (HMAC-SHA256): <c>v1=hex(HMAC(secret, "{zaman}.{gövde}"))</c>. Zaman damgası tekrar saldırısını sınırlar (tolerans içinde olmalı);
/// karşılaştırma sabit sürelidir. Gizli anahtar yapılandırmadan gelir, koda yazılmaz.
/// </summary>
public static class SignatureWebhook
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Compute(string secret, string timestamp, byte[] body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data, prefix.Length);
        return "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    /// <summary>İmza geçerli ve zaman damgası toleransta mı? Başarısızlık nedeni yanıta yansıtılmaz (yalnızca günlük için döner).</summary>
    public static (bool Valid, string? Failure) Verify(string secret, string? timestamp, string? signature, byte[] body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return (false, "Gizli anahtar yapılandırılmamış.");
        }

        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
        {
            return (false, "İmza ya da zaman damgası başlığı yok.");
        }

        if (!long.TryParse(timestamp, out var seconds))
        {
            return (false, "Zaman damgası geçersiz.");
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((now - sent).Duration() > Tolerance)
        {
            return (false, "Zaman damgası tolerans dışında (tekrar saldırısı ya da saat sapması).");
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, body));
        var given = Encoding.ASCII.GetBytes(signature.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, given) ? (true, null) : (false, "İmza uyuşmuyor.");
    }
}
