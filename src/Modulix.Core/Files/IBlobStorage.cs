namespace Modulix.Core.Files;

/// <summary>
/// Dosya içeriği depolama adaptörü (ADR-0040 K4). Üst veri veritabanındadır; içerik burada. Anahtar çağıranın verdiği rastgele dizgidir
/// (<see cref="StoredFile.NewStorageKey"/>); adaptör anahtarı yola çevirirken yol çıkışına izin vermez ve tenant başına ayrı bir kök kullanır.
/// </summary>
public interface IBlobStorage
{
    /// <summary>İçeriği yazar. Var olan bir anahtarı ezmez (<see cref="IOException"/>); yarım yazılmış içerik görünmez.</summary>
    Task PutAsync(Guid tenantId, string key, Stream content, CancellationToken cancellationToken);

    /// <summary>İçeriği açar; yoksa <c>null</c>.</summary>
    Task<Stream?> OpenReadAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    /// <summary>İçeriği siler; yoksa sessizce geçer.</summary>
    Task DeleteAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    /// <summary>Tenant'ın bütün içeriğini <paramref name="directory"/> altına, anahtar adıyla kopyalar (tenant dışa aktarma). Kopyalanan dosya sayısını döner.</summary>
    Task<int> ExportTenantAsync(Guid tenantId, string directory, CancellationToken cancellationToken);

    /// <summary>Tenant'ın bütün içeriğini siler (tenant silme). Silinen dosya sayısını döner.</summary>
    Task<int> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Dosya deposu yapılandırması (<c>Storage</c> bölümü).</summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Tek dosya için en büyük boyut (bayt). Varsayılan 10 MB.</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Yerel disk adaptörünün kökü. Boşsa uygulama veri dizini altında <c>blobs</c>. Web köküne ve statik dosya sunumuna dışında olmalıdır;
    /// yedeğe dahil edilmelidir (docs/tenant-isletim.md).
    /// </summary>
    public string? LocalRoot { get; set; }
}
