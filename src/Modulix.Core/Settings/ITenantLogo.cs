namespace Modulix.Core.Settings;

/// <summary>Logonun gösterim bilgisi: <see cref="Version"/> her değişiklikte artar (adres önbelleği bununla tazelenir).</summary>
public sealed record TenantLogoInfo(string ContentType, int Size, long Version);

/// <summary>
/// Aktif tenant'ın logosu (Y6b). Okuma her üyeye açıktır; yükleme ve kaldırma <c>core.settings.manage</c> ister (API
/// denetler) ve firmanın denetim kaydına "Firma bilgileri → Logo" olarak yazılır.
/// </summary>
public interface ITenantLogo
{
    Task<TenantLogoInfo?> GetInfoAsync(CancellationToken cancellationToken);

    Task<(TenantLogoInfo Info, byte[] Data)?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Dosya PNG/JPEG değilse veya büyükse <see cref="ArgumentException"/> (ParamName "logo").</summary>
    Task<TenantLogoInfo> SaveAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>Logo yoksa bir şey yapmaz.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}
