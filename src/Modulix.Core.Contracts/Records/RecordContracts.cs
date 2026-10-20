using System.Text.Json.Nodes;

namespace Modulix.Core.Contracts.Records;

// Kayıt boru hattının sözleşmeleri (talimat Bölüm 3, kural 1). API, arka plan işi, içe aktarma
// ve AI tool'ları kayıtlara yalnızca IRecordPipeline üzerinden erişir.

public enum RecordOperation
{
    Read = 1,
    Create = 2,
    Update = 3,
    Delete = 4,
}

/// <summary>İşlemi kim başlattı; audit kaydına yazılır.</summary>
public enum ExecutionSource
{
    Api = 1,
    BackgroundJob = 2,
    Import = 3,
    AiTool = 4,
}

/// <summary>Bir kaydın adresi. EntityKey "modül.entity" biçimindedir (ör. "crm.company").</summary>
public sealed record RecordRef(string EntityKey, Guid Id);

/// <summary>Kaydın dışarıya verilen hali. Alan adları teknik kimliklerdir (görünen ad değil).</summary>
public sealed record RecordData(string EntityKey, Guid Id, long Version, JsonObject Values);

/// <summary>Oluşturma veya güncelleme isteği. Güncellemede <see cref="ExpectedVersion"/> verilirse eşzamanlılık kontrol edilir.</summary>
public sealed record RecordCommand(
    string EntityKey,
    Guid? Id,
    JsonObject Values,
    long? ExpectedVersion = null,
    ExecutionSource Source = ExecutionSource.Api);

public sealed record RecordQuery(string EntityKey, int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 200;
}

public sealed record RecordPage(IReadOnlyList<RecordData> Items, int Page, int PageSize, int TotalCount);

public sealed record RecordFieldError(string Field, string Message);

public enum RecordErrorCode
{
    NoActiveTenant,
    UnknownEntity,
    FeatureNotLicensed,
    PermissionDenied,
    NotFound,
    ValidationFailed,
    Conflict,
}

public sealed record RecordError(RecordErrorCode Code, string Message, IReadOnlyList<RecordFieldError>? FieldErrors = null);

public sealed class RecordResult<T>
{
    private RecordResult(T? value, RecordError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public RecordError? Error { get; }

    public bool IsSuccess => Error is null;

    public static RecordResult<T> Ok(T value) => new(value, null);

    public static RecordResult<T> Fail(RecordErrorCode code, string message, IReadOnlyList<RecordFieldError>? fieldErrors = null) =>
        new(default, new RecordError(code, message, fieldErrors));
}

/// <summary>Kayıtlara erişimin tek yolu. Adımlar: tenant → lisans → yetki → veri kapsamı → doğrulama → kayıt → audit → olay.</summary>
public interface IRecordPipeline
{
    Task<RecordResult<RecordData>> GetAsync(RecordRef record, CancellationToken cancellationToken);

    Task<RecordResult<RecordPage>> QueryAsync(RecordQuery query, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CreateAsync(RecordCommand command, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> UpdateAsync(RecordCommand command, CancellationToken cancellationToken);

    Task<RecordResult<bool>> DeleteAsync(RecordRef record, long? expectedVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Bir entity'nin saklama uygulaması; modüller kendi entity'leri için yazar. Değişiklikleri yalnızca
/// hazırlar, kaydetmez: kayıt, audit ve olay birlikte, tek seferde yazılır (boru hattı + IUnitOfWork).
/// Tenant filtresi ve izolasyon saklama katmanında otomatik uygulanır.
/// </summary>
public interface IRecordStore
{
    string EntityKey { get; }

    /// <summary>Lisans kontrolünde kullanılan özellik (ör. "crm.company"); plan adı değil.</summary>
    string FeatureKey { get; }

    IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values);

    Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken);

    Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken);

    /// <summary>Kayıt yoksa null. Sürüm uyuşmazsa <see cref="RecordConflictException"/>.</summary>
    Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Kayıt yoksa false. Sürüm uyuşmazsa <see cref="RecordConflictException"/>.</summary>
    Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken);
}

/// <summary>Eşzamanlı değişiklik veya benzersizlik ihlali.</summary>
public sealed class RecordConflictException(string message, Exception? innerException = null) : Exception(message, innerException);
