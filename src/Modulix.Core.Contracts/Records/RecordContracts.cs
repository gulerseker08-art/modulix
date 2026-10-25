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
    ExecutionSource Source = ExecutionSource.Api)
{
    /// <summary>Değişikliğin nedeni; olaya yazılır (ör. <see cref="Events.EventReasons.Transfer"/>), abonelikler buna göre davranır.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Sayfalı liste isteği. <see cref="Search"/> verilirse kayıt deposu kendi arama alanlarında arar.
/// <see cref="Filters"/> ad → değer çiftleridir (ör. "companyId"); hangi filtrelerin geçerli olduğunu kayıt
/// deposu belirler, bilinmeyen filtre veya geçersiz değer doğrulama hatasıdır.
/// </summary>
public sealed record RecordQuery(
    string EntityKey,
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    IReadOnlyDictionary<string, string>? Filters = null)
{
    public const int MaxSearchLength = 100;

    public const int MaxPageSize = 200;

    public const int MaxFilters = 10;

    /// <summary>
    /// Veri kapsamı (Y8): doluysa yalnızca sorumlusu bu kullanıcılardan biri olan kayıtlar döner; sorumlusu boş kayıt
    /// dönmez. Boru hattı doldurur (çağıranın verdiği değer yok sayılır); sorumlu alanı olmayan depo bunu yok sayar.
    /// </summary>
    public IReadOnlySet<Guid>? Owners { get; init; }

    /// <summary>Ç22: sunucunun belirlediği erişilebilir şirketler; şirket kapsamlı depolar sorguda zorunlu uygular.</summary>
    public IReadOnlySet<Guid>? AccessibleCompanyIds { get; init; }

    /// <summary>
    /// Özel alan filtreleri (Y9a): özel alan anahtarı → değer. Boru hattı doğrular ve <see cref="Filters"/>'tan
    /// ("custom.anahtar" adıyla gelir) buraya taşır; depo özel alan JSON'unda eşitlikle süzer.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomFilters { get; init; }
}

/// <summary>Kayıt değerlerindeki ortak alan adları.</summary>
public static class RecordFields
{
    /// <summary>
    /// Özel alanlar (Y9a): <c>{ "anahtar": değer }</c> nesnesi. Boru hattı tanımlara göre doğrular ve birleştirir; depo
    /// nesneyi olduğu gibi saklar ve okurken aynı adla verir.
    /// </summary>
    public const string Custom = "custom";

    /// <summary>Hata ve filtre adlarında özel alanın öneki (ör. "custom.arac_tipi").</summary>
    public const string CustomPrefix = Custom + ".";
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

    /// <param name="source">İşlemi kim başlattı; denetim kaydına ve olaya yazılır.</param>
    Task<RecordResult<bool>> DeleteAsync(
        RecordRef record, long? expectedVersion, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api);
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

    /// <summary>
    /// İşlem için gereken izin (ör. "crm.companies.read"); null ise aktif tenant yeterlidir.
    /// İzin, modülü tenant için açık ve kullanıcının rollerinden birinde varsa geçer.
    /// </summary>
    string? PermissionFor(RecordOperation operation) => null;

    /// <summary>
    /// İşlem için yeterli izinler: biri yeterlidir (ör. ortak kayıtlar birden fazla modülün izniyle açılır, ADR-0006).
    /// Null ise <see cref="PermissionFor"/> geçerlidir; boş liste kimseye izin vermez.
    /// </summary>
    IReadOnlyList<string>? AnyPermissionFor(RecordOperation operation) => null;

    /// <summary>
    /// Sorumlu alanının adı (değeri kullanıcı kimliği); varsa veri kapsamı (Y8) bu alana göre uygulanır ve depo
    /// <see cref="RecordQuery.Owners"/>'ı sorguda uygulamalıdır. Null ise kayıt türü kapsamdan etkilenmez.
    /// </summary>
    string? OwnerField => null;

    /// <summary>Ç22: yalnızca şirket kapsamlı kayıt depoları true döner; CompanyId alanı zorunludur.</summary>
    bool IsCompanyScoped => false;

    /// <summary>
    /// Özel alanları saklar mı (Y9a)? Öyleyse depo <see cref="RecordFields.Custom"/> değerini (boru hattının doğrulayıp
    /// birleştirdiği nesnenin tamamı) saklar, okurken aynı adla verir ve <see cref="RecordQuery.CustomFilters"/>'ı uygular.
    /// </summary>
    bool SupportsCustomFields => false;

    IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values);

    Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Filtre bilinmiyorsa veya değeri geçersizse <see cref="RecordValidationException"/>.</summary>
    Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken);

    /// <summary>Veriye bağlı bir kural geçmezse (ör. bağlı kayıt yok) <see cref="RecordValidationException"/>.</summary>
    Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken);

    /// <summary>
    /// Kayıt yoksa null. Sürüm uyuşmazsa <see cref="RecordConflictException"/>; veriye bağlı bir kural
    /// geçmezse <see cref="RecordValidationException"/>.
    /// </summary>
    Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Kayıt yoksa false. Sürüm uyuşmazsa <see cref="RecordConflictException"/>.</summary>
    Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken);
}

/// <summary>Eşzamanlı değişiklik veya benzersizlik ihlali.</summary>
public sealed class RecordConflictException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Yalnızca veriye bakarak anlaşılabilen doğrulama hatası (ör. seçilen firma bu tenant'ta yok).</summary>
public sealed class RecordValidationException(IReadOnlyList<RecordFieldError> errors)
    : Exception("Doğrulama hatası.")
{
    public IReadOnlyList<RecordFieldError> Errors { get; } = errors;
}
