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

    /// <summary>Kaydı başka bir kayda birleştirme (ADR-0013); yazma izninden ayrı bir izin ister.</summary>
    Merge = 5,
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

    /// <summary>
    /// Anonim oluşturma (ADR-0042 bölüm 24: tam anonim anket cevabı). Yalnızca <see cref="IRecordStore.AllowsAnonymousWrites"/> diyen depoda
    /// kabul edilir. Yetki ve doğrulama her zamanki gibi işler, ama yazan kişi hiçbir yere bağlanmaz: denetim satırı kullanıcısız, kayıt
    /// kimliksiz, değersiz ve iz kimliksizdir, zamanı yalnızca gündür; olay yayımlanmaz. "Her önemli yazma denetim kaydına yazılır" kuralının
    /// bilinçli istisnasıdır: yazmanın olduğu denetlenir, kimin ve neyin yazdığı denetlenmez.
    /// </summary>
    public bool Anonymous { get; init; }
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
    /// <see cref="Bulk"/> okumada en büyük sayfa. Sayfa başına sabit bir maliyet (yetki, kapsam, alan çözümü, iki sorgu) olduğundan çok
    /// kayıt okuyan rapor ve dışa aktarma bunu kullanır (ölçüm: tek sayfa ≈25 ms sabit, 200 kayıt ≈ 0,1 ms/kayıt).
    /// </summary>
    public const int MaxBulkPageSize = 1000;

    /// <summary>
    /// Toplu okuma (rapor, dışa aktarma): sayfa boyutu <see cref="MaxBulkPageSize"/>'a kadar olabilir. İzin, veri kapsamı, şirket kapsamı ve
    /// hassas alan kuralları aynen uygulanır; yalnızca sayfa üst sınırı değişir.
    /// </summary>
    public bool Bulk { get; init; }

    /// <summary>
    /// Veri kapsamı (Y8): doluysa yalnızca sorumlusu bu kullanıcılardan biri olan kayıtlar döner; sorumlusu boş kayıt
    /// dönmez. Boru hattı doldurur (çağıranın verdiği değer yok sayılır); sorumlu alanı olmayan depo bunu yok sayar.
    /// </summary>
    public IReadOnlySet<Guid>? Owners { get; init; }

    /// <summary>
    /// Şirket kapsamı (ADR-0008, adım 3): doluysa yalnızca <see cref="IRecordStore.CompanyField"/> değeri bu şirketlerden
    /// biri olan kayıtlar döner. Boş küme hiçbir kayıt döndürmez. Boru hattı doldurur (çağıranın verdiği değer yok sayılır);
    /// null ise süzgeç yoktur (ortak depo, ya da bütün şirketlere erişen yönetici/arka plan bağlamı). Şirket kapsamlı depo
    /// bunu <see cref="IRecordStore.CompanyField"/> üzerinden sorguda uygular.
    /// </summary>
    public IReadOnlySet<Guid>? Companies { get; init; }

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

public sealed record RecordFieldError(string Field, string Message)
{
    /// <summary>Hatanın makinece okunan türü; ileti değişse de içe aktarma gibi çağıranlar ayırt edebilsin diye. Çoğu hatada boştur.</summary>
    public string? Code { get; init; }
}

/// <summary><see cref="RecordFieldError.Code"/> değerleri.</summary>
public static class RecordFieldErrorCodes
{
    /// <summary>Aynı kesin anahtarla kayıt var (ör. vergi numarası).</summary>
    public const string Duplicate = "duplicate";
}

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

    /// <summary>
    /// Birleştirme önizlemesi (ADR-0013): alan farkları, etkilenen kayıtlar ve engeller. Yazmaz. Kaynak ya da hedef
    /// çağıranın kapsamında değilse "bulunamadı".
    /// </summary>
    Task<RecordResult<MergePreview>> PreviewMergeAsync(string entityKey, MergeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Kaynak kaydı hedef kayda birleştirir: tek işlemde bağlı kayıtlar taşınır, kaynak kaldırılır; her iki kayıt için audit ve
    /// olay yazılır. Sürüm uyuşmazlığı, çözülemeyen bağlılık veya yetki eksikliğinde hiçbir şey yazılmaz.
    /// </summary>
    Task<RecordResult<MergeResult>> MergeAsync(
        string entityKey, MergeRequest request, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api);

    /// <summary>Eski (birleştirilmiş) bir kimliğin hangi kayda gittiği; hedefi çağıran göremiyorsa "bulunamadı".</summary>
    Task<RecordResult<MergeLink>> ResolveMergedAsync(RecordRef record, CancellationToken cancellationToken);

    /// <summary>
    /// Birden fazla kayıt işlemini tek veritabanı işleminde çalıştırır (ör. Lead'in firma, kişi ve fırsata dönüşümü). İçeride
    /// çağrılan her boru hattı işlemi kendi izin, kapsam, audit ve olay adımlarından geçer; ancak hiçbiri <paramref name="work"/>
    /// başarıyla bitmeden kalıcı olmaz. Sonuç başarısızsa ya da istisna fırlarsa işlemdeki tüm yazımlar geri alınır.
    /// Zaten açık bir işlem varsa (ör. olay aboneleri) ona katılır ve kararı dış işlem verir.
    /// </summary>
    Task<RecordResult<T>> RunAtomicAsync<T>(Func<CancellationToken, Task<RecordResult<T>>> work, CancellationToken cancellationToken);
}

/// <summary>Tamamlanan birleştirme: hedefin yeni hali ve etkilenen kayıtların özeti.</summary>
public sealed record MergeResult(RecordData Target, Guid SourceId, IReadOnlyList<MergeImpact> Impacts);

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

    /// <summary>
    /// Kayıt, veri kapsamındaki sorumlulara (<paramref name="owners"/>) göre görünür mü? Varsayılan: <see cref="OwnerField"/>
    /// değeri kapsamdaysa. Görünürlüğü başka kayıtlara da bağlı olan depo (ör. kişi, bağlı olduğu firmanın kapsamıyla)
    /// bunu kendisi belirler; o zaman <see cref="RecordQuery.Owners"/> da sorguda aynı kuralla uygulanmalıdır.
    /// </summary>
    Task<bool> IsInOwnerScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        var inScope = OwnerField is { } field
            && record.Values.TryGetPropertyValue(field, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var owner)
            && owners.Contains(owner);
        return Task.FromResult(inScope);
    }

    /// <summary>Veri kapsamı bu depoda uygulanır mı? Varsayılan: sorumlu alanı varsa.</summary>
    bool UsesOwnerScope => OwnerField is not null;

    /// <summary>
    /// Depo anonim oluşturmayı (<see cref="RecordCommand.Anonymous"/>) kabul eder mi? Varsayılan hayır: anonim yazma denetim kuralının istisnasıdır,
    /// yalnızca kimliği hiçbir yerde saklanmaması gereken kayıt türü (tam anonim anket cevabı) açar.
    /// </summary>
    bool AllowsAnonymousWrites => false;

    /// <summary>
    /// Veri kapsamının hangi kayıt türüne göre çözüleceği (ADR-0030): rolün kapsamı ve platform tavanı bu anahtarla aranır.
    /// Varsayılan kendi anahtarıdır. Görünürlüğü ana kaydına bağlı alt kayıt deposu (ör. fırsat iletişimcisi, aktivite notu)
    /// ana türünün anahtarını verir; böylece alt kayıt ana kaydın kapsamını izler.
    /// </summary>
    string ScopeEntityKey => EntityKey;

    /// <summary>
    /// Şirket kimliği alanının adı (ADR-0008, adım 3); kayıt türü şirket kapsamlıysa (<see cref="Tenancy.ICompanyScoped"/>)
    /// verilir, değeri bir işletici şirketin kimliğidir. Varsa boru hattının şirket kapsam adımı bu alana göre uygulanır:
    /// okumada depo <see cref="RecordQuery.Companies"/>'i sorguda uygulamalı, yazmada boru hattı değerin erişilebilir
    /// olduğunu doğrular ve güncellemede değişmesine izin vermez. Olay zarfı da bu alandan <c>CompanyId</c>'yi alır.
    /// Null ise kayıt türü ortaktır ve şirket kapsamından etkilenmez.
    /// </summary>
    string? CompanyField => null;

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

/// <summary>
/// Kayıt türünde izin olduğu hâlde bu kayıt üzerinde işlem yetkisi yok (ör. süreli belgenin sahip kaydında güncelleme izni yok); boru hattı
/// "yetkisiz" (403) döner. Kaydın varlığı zaten görünürken kullanılır; görünmeyen kayıt için depo "bulunamadı" döner.
/// </summary>
public sealed class RecordAccessDeniedException(string message) : Exception(message);

/// <summary>
/// Kayıtları başka bir listede tanıtmak için kısa ad (ör. araç → plaka, sürücü → kişinin adı). Süreli belge listesi sahip kaydı bununla gösterir
/// (ADR-0038 dilim 2b). Çağıran kayıt türünü okuma iznini önceden denetler; bulunmayan kimlik sonuçta yer almaz.
/// </summary>
public interface IRecordLabelProvider
{
    Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>
/// Kayıt türü iznine ek olarak kayıt düzeyinde yazma kuralı olan depolar (ör. süreli belge: sahip kaydın güncelleme izni ve modül lisansı) bunu uygular.
/// Kayda bağlı yan yazmalar (dosya ekleme ve silme, ADR-0040) tür izninden sonra bunu da sorar; dönen ileti doluysa işlem yetkisizdir.
/// </summary>
public interface IRecordWriteGuard
{
    Task<string?> WriteDeniedAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Yalnızca veriye bakarak anlaşılabilen doğrulama hatası (ör. seçilen firma bu tenant'ta yok).</summary>
public sealed class RecordValidationException(IReadOnlyList<RecordFieldError> errors)
    : Exception("Doğrulama hatası.")
{
    public IReadOnlyList<RecordFieldError> Errors { get; } = errors;
}

/// <summary>
/// Şu an boru hattından geçen yazma komutunun nedeni (<see cref="RecordCommand.Reason"/>). Kayıt depoları, yalnızca güvenilir iç
/// çağıranların (ör. kayıt devri, <see cref="Events.EventReasons.Transfer"/>) verebildiği nedene göre davranışı ayarlayabilir; uç
/// noktalar neden vermez, istemci veremez. Yazma dışında ya da neden verilmediyse boştur.
/// </summary>
public interface IRecordCommandContext
{
    string? Reason { get; }
}
