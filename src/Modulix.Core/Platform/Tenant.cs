using System.Text.RegularExpressions;

namespace Modulix.Core.Platform;

public enum TenantStatus
{
    Active = 1,

    /// <summary>Kapatıldı: kullanıcılar giriş yapamaz, veri silinmez (talimat: kapatma ve silme ayrıdır).</summary>
    Closed = 2,

    /// <summary>Deneme: erişim açıktır, <see cref="Active"/> gibi davranır (ADR-0028). Süre sonu davranışı sonraki dilimdedir.</summary>
    Trial = 3,

    /// <summary>Askıda: giriş ve erişim durur, veri silinmez; <see cref="Closed"/> gibi davranır (ADR-0028). Kapatmadan farkı geçici olmasıdır.</summary>
    Suspended = 4,
}

/// <summary>
/// Durumların erişim anlamı tek yerde: firmanın kullanıcıları, işleyicileri ve genel uç noktaları için "çalışan firma".
/// Yeni durum eklenirken karar buraya yazılır; <c>Status == TenantStatus.Active</c> karşılaştırması kullanılmaz.
/// </summary>
public static class TenantStatusRules
{
    /// <summary>Erişimin açık olduğu durumlar. EF sorgularında <c>Operational.Contains(t.Status)</c> olarak çevrilir.</summary>
    public static readonly TenantStatus[] Operational = [TenantStatus.Active, TenantStatus.Trial];

    public static bool IsOperational(TenantStatus status) => status is TenantStatus.Active or TenantStatus.Trial;
}

/// <summary>
/// Platformdaki bir müşteri firma. Platform geneli kayıttır; tenant verisi değildir. Bütün tenant'lar tek
/// veritabanındadır (ADR-0020); ayrı bir kurulum (provisioning) adımı yoktur, kayıt oluşunca iş verisi yazılabilir.
/// </summary>
public sealed partial class Tenant
{
    public const int NameMaxLength = 200;
    public const int SlugMaxLength = 50;

    private Tenant()
    {
        // EF Core için
    }

    public Tenant(string name, string slug, bool trial = false)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        Slug = NormalizeSlug(slug);
        Status = trial ? TenantStatus.Trial : TenantStatus.Active;
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

    /// <summary>Erişim açık mı (Active veya Trial); bkz. <see cref="TenantStatusRules"/>.</summary>
    public bool IsActive => TenantStatusRules.IsOperational(Status);

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

    /// <summary>Kapalı veya askıdaki firmayı açar (Active). Önceki durum (deneme) tutulmaz.</summary>
    public void Reopen() => Status = TenantStatus.Active;

    /// <summary>Çalışan firmayı askıya alır; yalnızca Active veya Trial askıya alınır.</summary>
    public void Suspend()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("Yalnızca açık firma askıya alınabilir.");
        }

        Status = TenantStatus.Suspended;
    }

    /// <summary>Denemedeki firmayı etkinleştirir (Trial → Active).</summary>
    public void Activate()
    {
        if (Status != TenantStatus.Trial)
        {
            throw new InvalidOperationException("Yalnızca deneme firması etkinleştirilir.");
        }

        Status = TenantStatus.Active;
    }

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

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,48})[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
