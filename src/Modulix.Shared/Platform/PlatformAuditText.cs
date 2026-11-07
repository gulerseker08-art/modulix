namespace Modulix.Shared.Platform;

/// <summary>Platform denetim kaydındaki işlem anahtarlarının ekran metinleri (ADR-0028).</summary>
public static class PlatformAuditText
{
    /// <summary>Süzgeç listesi: anahtar ve metin, ekrandaki sırayla.</summary>
    public static readonly IReadOnlyList<(string Action, string Text)> All =
    [
        ("tenant.created", "Firma açıldı"),
        ("tenant.renamed", "Ad değişti"),
        ("tenant.closed", "Firma kapatıldı"),
        ("tenant.reopened", "Firma yeniden açıldı"),
        ("tenant.suspended", "Firma askıya alındı"),
        ("tenant.activated", "Deneme etkinleştirildi"),
        ("tenant.module-changed", "Modül hakkı değişti"),
        ("tenant.administrator-invited", "Yönetici davet edildi"),
        ("tenant.profile-applied", "Sektör profili uygulandı"),
        ("user.two-factor-reset", "İki adımlı doğrulama sıfırlandı"),
        ("tenant.support-access-granted", "Destek erişimi açıldı"),
        ("tenant.support-access-revoked", "Destek erişimi kapatıldı"),
        ("tenant.plan-assigned", "Plan atandı"),
        ("tenant.license-applied", "Lisans dosyası uygulandı"),
    ];

    public static string Of(string action) => All.FirstOrDefault(a => a.Action == action).Text ?? action;
}
