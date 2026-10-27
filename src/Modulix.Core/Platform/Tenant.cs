using System.Text.RegularExpressions;

namespace Modulix.Core.Platform;

public enum TenantStatus
{
    Active = 1,

    /// <summary>Kapatıldı: kullanıcılar giriş yapamaz, veri silinmez (talimat: kapatma ve silme ayrıdır).</summary>
    Closed = 2,
}

/// <summary>
/// Tenant iş veritabanının kurulum durumu (ADR-0009). İş yaşam döngüsünden (<see cref="TenantStatus"/>) ayrıdır:
/// bir tenant iş olarak açık (<c>Active</c>) olsa bile veritabanı kurulmadan iş API'lerine erişemez.
/// </summary>
public enum TenantProvisioningStatus
{
    /// <summary>Platform kaydı var, veritabanı henüz kurulmadı.</summary>
    Pending = 0,

    /// <summary>Kurulum sürüyor; eşzamanlı ikinci kurulum engellenir.</summary>
    Provisioning = 1,

    /// <summary>Kurulum tamamlandı; iş API'lerine erişim açıktır.</summary>
    Active = 2,

    /// <summary>Kurulum başarısız; güvenle yeniden denenebilir (veritabanı otomatik silinmez).</summary>
    Failed = 3,

    /// <summary>İdari olarak askıya alındı; veritabanına erişim reddedilir, veri silinmez.</summary>
    Suspended = 4,
}

/// <summary>Platformdaki bir müşteri firma. Platform geneli kayıttır; tenant verisi değildir.</summary>
public sealed partial class Tenant
{
    public const int NameMaxLength = 200;
    public const int SlugMaxLength = 50;

    private Tenant()
    {
        // EF Core için
    }

    public Tenant(string name, string slug)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        Slug = NormalizeSlug(slug);
        Status = TenantStatus.Active;
        ProvisioningStatus = TenantProvisioningStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Adreslerde kullanılan kısa ad (ör. "acme"). Değişmez.</summary>
    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>En son uygulanan sektör profili (Y9c); profil uygulanmadıysa null.</summary>
    public string? ProfileKey { get; private set; }

    public DateTimeOffset? ProfileAppliedAt { get; private set; }

    /// <summary>
    /// Tenant'ın iş veritabanının adı (ADR-0009). Platform defterinde tutulur; bağlantı cümlesi bu addan ve şablon
    /// bağlantıdan türetilir. Yalnızca güvenli karakterler içerir (<see cref="DatabaseNamePattern"/>); istemci seçemez.
    /// </summary>
    public string? DatabaseName { get; private set; }

    /// <summary>İş veritabanının kurulum durumu (ADR-0009).</summary>
    public TenantProvisioningStatus ProvisioningStatus { get; private set; }

    /// <summary>En son uygulanan tenant migration kimliği; şema sürümü (ADR-0009). Kurulmadıysa <c>null</c>.</summary>
    public string? SchemaVersion { get; private set; }

    public bool IsActive => Status == TenantStatus.Active;

    /// <summary>İş veritabanı kurulu ve erişime uygun mu (ADR-0009): kurulum tamamlanmış ve askıya alınmamış.</summary>
    public bool IsProvisioned => ProvisioningStatus == TenantProvisioningStatus.Active;

    /// <summary>Veritabanı adını atar (yalnızca kurulmadan önce, bir kez). Güvenli ad üretimi çağıranın sorumluluğundadır.</summary>
    public void AssignDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (!DatabaseNamePattern().IsMatch(databaseName))
        {
            throw new ArgumentException("Veritabanı adı yalnızca harf, rakam ve alt çizgi içerebilir.", nameof(databaseName));
        }

        DatabaseName = databaseName;
    }

    /// <summary>Kurulumu başlatır: yalnızca <c>Pending</c> veya <c>Failed</c> durumundan (yeniden deneme). Eşzamanlılık kilidi ayrıca gerekir.</summary>
    public void BeginProvisioning()
    {
        if (ProvisioningStatus is not (TenantProvisioningStatus.Pending or TenantProvisioningStatus.Failed))
        {
            throw new InvalidOperationException($"Kurulum yalnızca Pending/Failed durumundan başlatılır (şu an: {ProvisioningStatus}).");
        }

        ProvisioningStatus = TenantProvisioningStatus.Provisioning;
    }

    /// <summary>Kurulumu tamamlar ve şema sürümünü kaydeder.</summary>
    public void MarkProvisioned(string? schemaVersion)
    {
        ProvisioningStatus = TenantProvisioningStatus.Active;
        SchemaVersion = schemaVersion;
    }

    /// <summary>Kurulumu başarısız işaretler; veritabanı silinmez, yeniden denenebilir.</summary>
    public void MarkProvisioningFailed() => ProvisioningStatus = TenantProvisioningStatus.Failed;

    /// <summary>Uygulanan tenant migration kimliğini günceller (migration runner).</summary>
    public void RecordSchemaVersion(string? schemaVersion) => SchemaVersion = schemaVersion;

    /// <summary>Profilin uygulandığını kaydeder. Profil firma verisini yalnızca ekler; burada yalnızca anahtarı tutulur.</summary>
    public void ApplyProfile(string profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ProfileKey = profileKey;
        ProfileAppliedAt = DateTimeOffset.UtcNow;
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Tenant adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }

    public void Close() => Status = TenantStatus.Closed;

    public void Reopen() => Status = TenantStatus.Active;

    public static string NormalizeSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var normalized = slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Kısa ad 3-50 karakter olmalı; yalnızca a-z, 0-9 ve tire içerebilir, tire ile başlayıp bitemez.",
                nameof(slug));
        }

        return normalized;
    }

    /// <summary>Askıya alır (ADR-0009): yalnızca kurulmuş tenant. Veritabanına erişim reddedilir, veri silinmez.</summary>
    public void Suspend()
    {
        if (ProvisioningStatus != TenantProvisioningStatus.Active)
        {
            throw new InvalidOperationException("Yalnızca kurulumu tamamlanmış tenant askıya alınabilir.");
        }

        ProvisioningStatus = TenantProvisioningStatus.Suspended;
    }

    /// <summary>Askıdan alır (ADR-0009): veritabanı yerindedir, erişim yeniden açılır.</summary>
    public void Resume()
    {
        if (ProvisioningStatus != TenantProvisioningStatus.Suspended)
        {
            throw new InvalidOperationException("Yalnızca askıdaki tenant yeniden etkinleştirilebilir.");
        }

        ProvisioningStatus = TenantProvisioningStatus.Active;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,48})[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}
