namespace Modulix.Shared.Files;

/// <summary>
/// Bir kayda bağlı dosyanın üst verisi (ADR-0040). İçerik <c>/api/files/{id}/content</c> ile indirilir; depolama anahtarı ve yol hiçbir yanıtta yoktur.
/// <c>ScanStatus</c>: <c>NotScanned</c> (taranmadı), ileride <c>Pending</c>, <c>Clean</c>, <c>Infected</c>.
/// </summary>
public sealed record StoredFileDto(
    Guid Id,
    long Version,
    string EntityKey,
    Guid RecordId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string ScanStatus,
    Guid? UploadedByUserId,
    DateTimeOffset CreatedAt);
