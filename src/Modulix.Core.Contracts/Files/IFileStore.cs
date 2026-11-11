using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Contracts.Files;

/// <summary>Bir kayda bağlı dosyanın üst verisi (ADR-0040). İçerik ve depolama anahtarı burada yoktur.</summary>
public sealed record StoredFileInfo(
    Guid Id,
    long Version,
    string EntityKey,
    Guid RecordId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Classification,
    string ScanStatus,
    Guid? UploadedByUserId,
    DateTimeOffset CreatedAt);

/// <summary>İndirilecek dosya. Akışı çağıran kapatır.</summary>
public sealed record StoredFileContent(StoredFileInfo Info, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Kayıtlara bağlı dosyalar (ADR-0040). Erişim bağlı kaydın erişimidir: listeleme ve indirme kaydı okuyabilene, yükleme ve silme kaydı
/// güncelleyebilene (ve modülün yazılabilir olduğu evrede) açıktır. Kaydı göremeyen için dosya "bulunamadı"dır; dosya kimliğini bilmek yetki vermez.
/// Tenant yalnızca oturumdan gelir.
/// </summary>
public interface IFileStore
{
    Task<RecordResult<IReadOnlyList<StoredFileInfo>>> ListAsync(RecordRef record, CancellationToken cancellationToken);

    /// <summary>
    /// Dosyayı kayda ekler. <paramref name="content"/> en çok <see cref="IFileLimits.MaxFileBytes"/> bayt okunur; fazlası doğrulama hatasıdır.
    /// Aynı kayda aynı içerik ikinci kez eklenemez (çakışma).
    /// </summary>
    Task<RecordResult<StoredFileInfo>> AddAsync(RecordRef record, string? fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>İçeriği açar. <c>Pending</c> ve <c>Infected</c> taramalı dosyalar ile bilinmeyen sınıftaki dosyalar indirilemez.</summary>
    Task<RecordResult<StoredFileContent>> OpenAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Dosyayı siler (üst veri ve içerik). Eski sürüm çakışmadır.</summary>
    Task<RecordResult<bool>> DeleteAsync(Guid fileId, long version, CancellationToken cancellationToken);
}

/// <summary>Yükleme sınırları (yapılandırmadan). Uç noktalar gövdeyi okumadan önce boyutu denetlemek için kullanır.</summary>
public interface IFileLimits
{
    long MaxFileBytes { get; }
}
