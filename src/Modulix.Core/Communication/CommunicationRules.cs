using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Communication;

/// <summary>
/// İletişim tercihi kuralları (ADR-0025). Saf sınıf: veritabanına dokunmaz, birim testle sabit girdilerle sınanır.
/// </summary>
public static class CommunicationRules
{
    /// <summary>Birden fazla engel varsa gösterilen neden sırası: teslim edilemezlik izin sorunundan önce gelir.</summary>
    private static readonly SuppressionReason[] ReasonPriority =
    [
        SuppressionReason.HardBounce,
        SuppressionReason.InvalidAddress,
        SuppressionReason.Complaint,
        SuppressionReason.OptOut,
        SuppressionReason.Manual,
    ];

    /// <summary>
    /// Engel nedeni hangi amaçlara uygulanır? Teslim edilemeyen adrese hiçbir amaçla gönderilmez; şikâyet, ret ve elle engel
    /// pazarlamayı durdurur ama işlem/hizmet bildirimini (ör. şifre sıfırlama, talep yanıtı) durdurmaz.
    /// </summary>
    public static bool Applies(SuppressionReason reason, CommunicationPurpose purpose) =>
        reason is SuppressionReason.HardBounce or SuppressionReason.InvalidAddress || purpose == CommunicationPurpose.Marketing;

    /// <summary>
    /// Karar: önce engel (izni ezer), sonra amaç. İşlem/hizmet bildirimi izin gerektirmez (hukuki dayanak ayrı bir üründür,
    /// ADR-0025 bölüm 8); pazarlama yalnızca açık <see cref="ConsentStatus.Granted"/> ile gider, <see cref="ConsentStatus.Unknown"/> gitmez.
    /// </summary>
    public static CommunicationDecision Decide(
        CommunicationPurpose purpose, ConsentStatus status, IReadOnlyCollection<SuppressionReason> activeSuppressions)
    {
        foreach (var reason in ReasonPriority)
        {
            if (activeSuppressions.Contains(reason) && Applies(reason, purpose))
            {
                return new CommunicationDecision(CommunicationOutcome.Denied, CommunicationCodes.Suppressed(reason), SuppressionMessage(reason));
            }
        }

        if (purpose != CommunicationPurpose.Marketing)
        {
            return new CommunicationDecision(CommunicationOutcome.Allowed, CommunicationCodes.NoConsentRequired, "İşlem ve hizmet bildirimi izin gerektirmez.");
        }

        return status switch
        {
            ConsentStatus.Granted => new CommunicationDecision(CommunicationOutcome.Allowed, CommunicationCodes.Granted, "Pazarlama izni var."),
            ConsentStatus.Denied => new CommunicationDecision(CommunicationOutcome.Denied, CommunicationCodes.Denied, "Pazarlama iletişimi reddedilmiş."),
            _ => new CommunicationDecision(CommunicationOutcome.Unknown, CommunicationCodes.Unknown, "Pazarlama izni bilinmiyor; gönderilmez."),
        };
    }

    /// <summary>
    /// İzin değişikliği doğrulaması; hata metni ya da null. Kurallar: durum yalnızca Granted/Denied kaydedilir; izin için kanıt
    /// referansı ya da gerekçe zorunludur; reddedilmiş izni yalnızca kişinin kendisi ya da yetkili kullanıcı (form, tercih merkezi,
    /// elle) ve kanıtla geri açabilir. İçe aktarma, sağlayıcı ve sistem ret kaydını ezemez.
    /// </summary>
    public static string? ValidateChange(ConsentStatus current, ConsentStatus requested, string source, string? evidenceRef, string? reason)
    {
        if (!ConsentSources.All.Contains(source))
        {
            return "Kaynak geçersiz.";
        }

        if (requested == ConsentStatus.Unknown)
        {
            return "Durum yalnızca 'izin var' ya da 'reddedildi' olarak kaydedilir.";
        }

        if (evidenceRef?.Length > CommunicationPreference.EvidenceMaxLength)
        {
            return $"Kanıt referansı en fazla {CommunicationPreference.EvidenceMaxLength} karakter olabilir.";
        }

        if (reason?.Length > CommunicationPreference.ReasonMaxLength)
        {
            return $"Gerekçe en fazla {CommunicationPreference.ReasonMaxLength} karakter olabilir.";
        }

        if (requested == ConsentStatus.Granted)
        {
            if (string.IsNullOrWhiteSpace(evidenceRef) && string.IsNullOrWhiteSpace(reason))
            {
                return "İzin kaydı için kanıt referansı ya da gerekçe zorunludur.";
            }

            if (current == ConsentStatus.Denied)
            {
                if (!ConsentSources.CanReGrant.Contains(source))
                {
                    return "Reddedilmiş izin içe aktarma, sağlayıcı ya da sistem kaynağıyla yeniden açılamaz.";
                }

                if (string.IsNullOrWhiteSpace(evidenceRef))
                {
                    return "Reddedilmiş izni yeniden açmak için kanıt referansı zorunludur.";
                }
            }
        }

        return null;
    }

    private static string SuppressionMessage(SuppressionReason reason) => reason switch
    {
        SuppressionReason.HardBounce => "Adres kalıcı olarak teslim edilemedi (engelli).",
        SuppressionReason.InvalidAddress => "Adres geçersiz (engelli).",
        SuppressionReason.Complaint => "Alıcı şikâyet etti (engelli).",
        SuppressionReason.OptOut => "Alıcı iletişim istemiyor (engelli).",
        _ => "Adres elle engellenmiş.",
    };
}
