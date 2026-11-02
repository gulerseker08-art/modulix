namespace Modulix.Core.Contracts.Communication;

// Ortak iletişim tercihi (ADR-0025): izin, ret ve engel kaydının tek sahibi çekirdektir. Pazarlama, destek gibi modüller
// tercihi yalnızca bu sözleşmeyle okur; kampanya yönetimi tercihin sahibi değildir. Tercih kişiye değil iletişim noktasına
// (kanal + normalize adres) bağlıdır; böylece CRM kapalıyken de çalışır ve ortak adres için "izin belirsiz" doğru kalır.

public enum CommunicationChannel
{
    Email = 1,
    Phone = 2,
    Sms = 3,
    WhatsApp = 4,
}

/// <summary>
/// İletişimin amacı. İşlem/hizmet bildirimi pazarlamadan ayrı bir amaçtır; pazarlama iznini aşmak için seçilemez
/// (pazarlama gönderimi her zaman <see cref="Marketing"/> ile değerlendirilir).
/// </summary>
public enum CommunicationPurpose
{
    Marketing = 1,
    Transactional = 2,
    Service = 3,
}

public enum ConsentStatus
{
    /// <summary>Kayıt yok ya da hiç sorulmadı. Firma/e-posta bulunması izin değildir; pazarlama gönderilmez.</summary>
    Unknown = 0,

    Granted = 1,
    Denied = 2,
}

public enum SuppressionReason
{
    /// <summary>Kalıcı teslim edilememe. Adres, bütün amaçlar için engellenir.</summary>
    HardBounce = 1,

    /// <summary>Alıcı iletiyi şikâyet etti (spam bildirimi). Pazarlama engellenir.</summary>
    Complaint = 2,

    /// <summary>Geçersiz ya da var olmayan adres. Bütün amaçlar için engellenir.</summary>
    InvalidAddress = 3,

    /// <summary>İletişim istemiyorum / abonelikten çıkma. Pazarlama engellenir.</summary>
    OptOut = 4,

    /// <summary>Yetkili kişinin elle koyduğu engel. Pazarlama engellenir.</summary>
    Manual = 5,
}

public enum CommunicationOutcome
{
    Allowed = 1,
    Denied = 2,

    /// <summary>İzin bilinmiyor; pazarlama için "gönderme" demektir.</summary>
    Unknown = 3,
}

/// <summary>Bir iletişim noktası için karar. <see cref="Code"/> kararlıdır (arayüz ve raporlar buna göre sayar).</summary>
public sealed record CommunicationDecision(CommunicationOutcome Outcome, string Code, string Message)
{
    public bool IsAllowed => Outcome == CommunicationOutcome.Allowed;
}

/// <summary>Karar kodları.</summary>
public static class CommunicationCodes
{
    public const string Granted = "granted";
    public const string Denied = "denied";
    public const string Unknown = "unknown";
    public const string NoConsentRequired = "no-consent-required";
    public const string InvalidAddress = "invalid-address";

    /// <summary>"suppressed:hard-bounce" gibi.</summary>
    public static string Suppressed(SuppressionReason reason) => "suppressed:" + reason switch
    {
        SuppressionReason.HardBounce => "hard-bounce",
        SuppressionReason.Complaint => "complaint",
        SuppressionReason.InvalidAddress => "invalid-address",
        SuppressionReason.OptOut => "opt-out",
        _ => "manual",
    };
}

/// <summary>
/// Aktif tenant'ın iletişim tercihlerini okur. Tenant bağlamı olmadan çalışmaz. Karar her çağrıda güncel kayıttan verilir
/// (önbellek yok): izin iptali ve engel, gönderimden hemen önce yapılan çağrıda görünür.
/// </summary>
public interface ICommunicationPreferences
{
    Task<CommunicationDecision> EvaluateAsync(
        CommunicationChannel channel, CommunicationPurpose purpose, string address, CancellationToken cancellationToken);

    /// <summary>Anahtar, çağıranın verdiği adrestir (aynen). Geçersiz adres <see cref="CommunicationCodes.InvalidAddress"/> ile reddedilir.</summary>
    Task<IReadOnlyDictionary<string, CommunicationDecision>> EvaluateManyAsync(
        CommunicationChannel channel, CommunicationPurpose purpose, IReadOnlyCollection<string> addresses, CancellationToken cancellationToken);
}

/// <summary>
/// İletişim noktası normalizasyonu: tercih ve engel kayıtlarının aranma anahtarıdır. Orijinal değer ayrıca (ör. CRM iletişim
/// noktasında) korunur. Her modül aynı kuralı kullansın diye burada durur.
/// </summary>
public static class CommunicationAddress
{
    public const int MaxLength = 254;

    private const int MinPhoneDigits = 8;
    private const int MaxPhoneDigits = 15;

    /// <summary>
    /// E-posta: kırpılır ve küçük harfe çevrilir; yerel kısım üzerinde başka bir birleştirme (nokta silme, "+etiket" atma) yapılmaz,
    /// çünkü bunlar farklı kişilerin adresi olabilir. Telefon: yalnızca rakamlar, uluslararası biçimde (artısız); "+" ya da "00"
    /// ile başlamayan 10 haneli (5xx…) ve 11 haneli (05xx…) numaralar Türkiye varsayılır ve "90" öneki alır.
    /// Geçersizse null.
    /// </summary>
    public static string? Normalize(CommunicationChannel channel, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return channel == CommunicationChannel.Email ? NormalizeEmail(raw) : NormalizePhone(raw);
    }

    /// <summary>Günlük ve denetim kaydı için maskelenmiş görünüm ("a***@example.com", "***4567").</summary>
    public static string Mask(CommunicationChannel channel, string address)
    {
        if (channel == CommunicationChannel.Email)
        {
            var at = address.IndexOf('@', StringComparison.Ordinal);
            return at <= 0 ? "***" : address[0] + "***" + address[at..];
        }

        return address.Length <= 4 ? "***" : "***" + address[^4..];
    }

    private static string? NormalizeEmail(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        if (value.Length > MaxLength || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            return null;
        }

        var domain = value[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal) && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..", StringComparison.Ordinal)
            ? value
            : null;
    }

    private static string? NormalizePhone(string raw)
    {
        var text = raw.Trim();
        var hasPlus = text.StartsWith('+');
        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        if (digits.Length == 0)
        {
            return null;
        }

        if (!hasPlus)
        {
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }
            else if (digits.Length == 11 && digits[0] == '0')
            {
                digits = "90" + digits[1..];
            }
            else if (digits.Length == 10)
            {
                digits = "90" + digits;
            }
        }

        return digits.Length is >= MinPhoneDigits and <= MaxPhoneDigits ? digits : null;
    }
}
