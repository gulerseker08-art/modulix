using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Communication;

/// <summary>
/// İletişim konusu (ADR-0025, dilim 14): firmanın tanımladığı pazarlama başlığı (ör. "bulten", "kampanya-duyurulari"). Konu yalnızca daraltır:
/// kişi bir konudan çıkabilir, ama konu tercihi pazarlama izni vermez; izin her zaman <see cref="CommunicationPreference"/>'tadır.
/// Anahtar oluşturulduktan sonra değişmez (kampanya ve tercih satırları anahtara bağlıdır); ad ve açık/kapalı değişir. Satır silinmez.
/// </summary>
public sealed class CommunicationTopic : ITenantEntity, ICompanyShared
{
    public const int KeyMaxLength = 40;
    public const int NameMaxLength = 100;

    private CommunicationTopic()
    {
        // EF Core için
    }

    public CommunicationTopic(string key, string name, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Key = key;
        Name = name;
        IsActive = true;
        CreatedAt = now;
        ChangedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Kapalı konu yeni kampanyada seçilemez ve portalda gösterilmez; mevcut tercihler korunur.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public void Apply(string name, bool isActive, DateTimeOffset now)
    {
        Name = name;
        IsActive = isActive;
        ChangedAt = now;
    }
}

/// <summary>
/// Bir iletişim noktasının bir konudan çıkış durumu. Satır yoksa konu açıktır (varsayılan: tüm konular). Kimlik: (tenant, kanal, normalize adres,
/// konu anahtarı). Değişiklik geçmişi denetim kaydındadır (adres maskeli).
/// </summary>
public sealed class CommunicationTopicPreference : ITenantEntity, ICompanyShared
{
    private CommunicationTopicPreference()
    {
        // EF Core için
    }

    public CommunicationTopicPreference(CommunicationChannel channel, string address, string topicKey, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Channel = channel;
        Address = address;
        TopicKey = topicKey;
        Source = ConsentSources.System;
        ChangedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CommunicationChannel Channel { get; private set; }

    public string Address { get; private set; } = string.Empty;

    public string TopicKey { get; private set; } = string.Empty;

    public bool OptedOut { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public Guid? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public void Apply(bool optedOut, string source, Guid? changedBy, DateTimeOffset now)
    {
        OptedOut = optedOut;
        Source = source;
        ChangedBy = changedBy;
        ChangedAt = now;
    }
}

/// <summary>Konu kuralları. Saf sınıf: veritabanına dokunmaz.</summary>
public static partial class TopicRules
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    /// <summary>Anahtar: küçük harf, rakam ve tire; ilk ve son karakter harf/rakam. Hata metni ya da null.</summary>
    public static string? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Konu anahtarı zorunludur.";
        }

        if (key.Length > CommunicationTopic.KeyMaxLength)
        {
            return $"Konu anahtarı en fazla {CommunicationTopic.KeyMaxLength} karakter olabilir.";
        }

        return KeyPattern().IsMatch(key) ? null : "Konu anahtarı küçük harf, rakam ve tireden oluşmalıdır (ör. haber-bulteni).";
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Konu adı zorunludur.";
        }

        return name.Trim().Length > CommunicationTopic.NameMaxLength ? $"Konu adı en fazla {CommunicationTopic.NameMaxLength} karakter olabilir." : null;
    }

    /// <summary>
    /// Konu kararı: yalnızca pazarlamada ve yalnızca izin verilmiş bir karara uygulanır; zaten red/bilinmiyor/engel olan karar değişmez (asıl neden
    /// korunur). Konuda çıkış varsa karar <see cref="CommunicationCodes.TopicOptOut"/> ile reddedilir.
    /// </summary>
    public static CommunicationDecision ApplyTopic(CommunicationDecision decision, CommunicationPurpose purpose, bool topicOptedOut)
    {
        if (purpose != CommunicationPurpose.Marketing || !topicOptedOut || decision.Outcome != CommunicationOutcome.Allowed)
        {
            return decision;
        }

        return new CommunicationDecision(CommunicationOutcome.Denied, CommunicationCodes.TopicOptOut, "Alıcı bu konudan çıkmış.");
    }

    /// <summary>
    /// Kişinin kendi bağlantısından konuya yeniden abone olup olamayacağı: yalnızca kendi çıkışını (tercih merkezi) geri alabilir; firmanın kaydettiği
    /// çıkışı, aktif engeli ve reddedilmiş/bilinmeyen pazarlama iznini bağlantı değiştiremez (konu izin vermez). Hata metni ya da null.
    /// </summary>
    public static string? SelfResubscribeBlock(ConsentStatus consent, bool suppressed, string topicSource)
    {
        if (suppressed)
        {
            return "Bu adres için gönderim engellenmiş. Lütfen firmayla iletişime geçin.";
        }

        if (consent != ConsentStatus.Granted)
        {
            return "Konuya abone olmak için önce pazarlama izninin açık olması gerekir.";
        }

        return topicSource == ConsentSources.PreferenceCenter
            ? null
            : "Bu tercih firma tarafından kaydedilmiş. Lütfen firmayla iletişime geçin.";
    }
}
