using System.Text.RegularExpressions;

namespace Modulix.Core.Platform;

public enum TenantStatus
{
    Active = 1,

    /// <summary>Kapatıldı: kullanıcılar giriş yapamaz, veri silinmez (talimat: kapatma ve silme ayrıdır).</summary>
    Closed = 2,
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

    public Tenant(string name, string slug)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        Slug = NormalizeSlug(slug);
        Status = TenantStatus.Active;
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

    public bool IsActive => Status == TenantStatus.Active;

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

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,48})[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
