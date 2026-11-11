using System.Security.Cryptography;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Files;

/// <summary>
/// Bir kayda bağlı dosyanın üst verisi (ADR-0040). İçerik veritabanında değil, depolama adaptöründedir (<see cref="IBlobStorage"/>);
/// <see cref="StorageKey"/> rastgeledir ve hiçbir uç nokta tarafından dışarı verilmez. Erişim, bağlı kaydın erişiminden türer.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class StoredFile : ITenantEntity, ICompanyShared
{
    public const int EntityKeyMaxLength = 100;
    public const int FileNameMaxLength = 200;
    public const int ContentTypeMaxLength = 100;
    public const int ClassificationMaxLength = 30;
    public const int ScanStatusMaxLength = 20;
    public const int StorageKeyLength = 64;

    private StoredFile()
    {
        // EF Core için
    }

    public StoredFile(string entityKey, Guid recordId, string fileName, string contentType, long sizeBytes, string sha256, Guid? uploadedByUserId)
    {
        Id = Guid.CreateVersion7();
        EntityKey = entityKey;
        RecordId = recordId;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = NewStorageKey();
        Classification = FileClassification.Internal;
        ScanStatus = FileScanStatus.NotScanned;
        UploadedByUserId = uploadedByUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        Version = 1;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Bağlı kaydın türü (ör. <c>crm.company</c>).</summary>
    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>İçeriğin SHA-256 özeti, büyük harf onaltılık.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Adaptördeki nesne anahtarı: 32 bayt kriptografik rastgele, onaltılık. Ad, kimlik ya da içerikten türetilmez.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>Erişim sınıfı (<see cref="FileClassification"/>).</summary>
    public string Classification { get; private set; } = string.Empty;

    /// <summary>Tarama durumu (<see cref="FileScanStatus"/>).</summary>
    public string ScanStatus { get; private set; } = string.Empty;

    public Guid? UploadedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Silme için eşzamanlılık belirteci.</summary>
    public long Version { get; private set; }

    public static string NewStorageKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(StorageKeyLength / 2)).ToLowerInvariant();
}

/// <summary>Dosyanın erişim sınıfı. Dilim 1'de yalnızca <see cref="Internal"/> vardır; portal/paylaşım dilimi yenilerini ekler.</summary>
public static class FileClassification
{
    /// <summary>Yalnızca personel.</summary>
    public const string Internal = "Internal";

    /// <summary>İndirmeye açık sınıflar; listede olmayan sınıf indirilemez.</summary>
    public static bool IsDownloadable(string classification) => classification == Internal;
}

/// <summary>Zararlı içerik taraması durumu. Dilim 1 yalnızca <see cref="NotScanned"/> yazar; <see cref="Pending"/> ve <see cref="Infected"/> indirilemez.</summary>
public static class FileScanStatus
{
    public const string NotScanned = "NotScanned";
    public const string Pending = "Pending";
    public const string Clean = "Clean";
    public const string Infected = "Infected";

    public static bool IsDownloadable(string status) => status is NotScanned or Clean;
}
