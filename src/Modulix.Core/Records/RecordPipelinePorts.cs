using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Records;

// Boru hattının adımları. Lisans (modül hakkı) ve yetki (rol izinleri) Infrastructure'daki
// uygulamalarla denetlenir; aşağıdaki "her şeye izin ver" sınıfları birim testleri içindir.
// Veri kapsamı v0'da tenant geneli; gerçek uygulaması sonra gelir. Boru hattı ve çağıranlar
// değişmez; yalnızca kayıtlı uygulama değişir.

public interface IEntitlementService
{
    Task<bool> CanUseAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken);
}

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken);
}

/// <summary>
/// Veri kapsamı (Y8): kişinin hangi kullanıcıların sorumlu olduğu kayıtları görebileceği. Boru hattı bunu sorumlu alanı
/// olan kayıt türlerine (<see cref="IRecordStore.OwnerField"/>) uygular: listede sorguda, tek kayıtta sorumluya bakarak.
/// </summary>
public interface IDataScopeService
{
    /// <summary>Null: tüm kayıtlar. Aksi halde yalnızca sorumlusu bu kullanıcılardan biri olan kayıtlar.</summary>
    Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Hassas alanlar (Y8b): kişinin bu kayıt türünde göremediği alanlar. Boru hattı bu alanları okumada çıkarır, yazmada
/// reddeder (boş değer gönderilmişse yok sayar, böylece kaydın değeri korunur) ve bu alanlara göre süzmeye izin vermez.
/// </summary>
public interface IFieldAccessService
{
    Task<IReadOnlySet<string>> GetHiddenFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken);
}

/// <summary>Özel alanlar (Y9a): kayıt türünün etkin (arşivlenmemiş) alan tanımları, sıraya göre. İstek boyunca saklanabilir.</summary>
public interface ICustomFieldProvider
{
    Task<IReadOnlyList<Customization.CustomFieldInfo>> GetFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken);
}

public sealed class NoCustomFields : ICustomFieldProvider
{
    public Task<IReadOnlyList<Customization.CustomFieldInfo>> GetFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Customization.CustomFieldInfo>>([]);
}

public sealed class AllFieldsVisible : IFieldAccessService
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public Task<IReadOnlySet<string>> GetHiddenFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
        Task.FromResult(None);
}

public sealed class AllowAllEntitlements : IEntitlementService
{
    public Task<bool> CanUseAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class AllowAllPermissions : IPermissionService
{
    public Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

public sealed class TenantWideDataScope : IDataScopeService
{
    public Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>?>(null);
}

public enum AuditAction
{
    Created = 1,
    Updated = 2,
    Deleted = 3,

    /// <summary>Kayıtlar dışa aktarıldı (ör. denetim kaydının CSV'si, Y5).</summary>
    Exported = 4,

    // Oturum olayları (Y5b): kayıt türü core.account, kayıt kişinin hesabıdır.
    SignedIn = 5,
    SignInFailed = 6,
    LockedOut = 7,
    PasswordReset = 8,
    InvitationAccepted = 9,

    // Hesap güvenliği (Y7a): kişinin kendi hesabındaki işlemler. Ad ve e-posta değişikliği Updated olarak yazılır.
    PasswordChanged = 10,
    PasswordResetRequested = 11,
    SessionsClosed = 12,

    // İki adımlı doğrulama (Y7b).
    TwoFactorEnabled = 13,
    TwoFactorDisabled = 14,
    RecoveryCodesRegenerated = 15,
    SignedInWithRecoveryCode = 16,
    TwoFactorReset = 17,

    /// <summary>Yetkisiz aktif şirket seçimi reddedildi (Ç22).</summary>
    CompanySelectionDenied = 18,
}

/// <summary>Audit kaydı: kim, ne, ne zaman. <see cref="Changes"/>: alan → { old, new }.</summary>
public sealed record AuditEntry(
    Guid TenantId,
    Guid UserId,
    string EntityKey,
    Guid RecordId,
    AuditAction Action,
    JsonObject Changes,
    ExecutionSource Source,
    string? CorrelationId)
{
    /// <summary>Değişikliği yapan olay aboneliği (ADR-0007, ör. "core.assignment-email"); kişinin değişikliğinde boş.</summary>
    public string? Subscription { get; init; }
}

/// <summary>
/// Outbox'a yazılacak olay (ADR-0007); zarfın alanları <see cref="Contracts.Events.EventEnvelope"/>'ta açıklanır.
/// Boru hattı doldurur; çağıranlar yalnızca nedeni verir (<see cref="RecordCommand.Reason"/>).
/// </summary>
public sealed record DomainEvent(Guid TenantId, string EventType, string EntityKey, Guid RecordId, JsonObject Payload)
{
    /// <summary>İşlemin işletici şirketi; ortak kayıtlarda boş olabilir.</summary>
    public Guid? CompanyId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public long? Sequence { get; init; }

    public Guid UserId { get; init; }

    public ExecutionSource Source { get; init; } = ExecutionSource.Api;

    public string? CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    public int Depth { get; init; }

    public string? Reason { get; init; }
}

public interface IAuditWriter
{
    /// <summary>Kaydı hazırlar; <see cref="IUnitOfWork.SaveChangesAsync"/> ile kaydın kendisiyle birlikte yazılır.</summary>
    void Add(AuditEntry entry);
}

public interface IDomainEventOutbox
{
    /// <summary>Olayı outbox'a hazırlar; kaydın kendisiyle aynı işlemde yazılır.</summary>
    void Add(DomainEvent domainEvent);
}

public interface IUnitOfWork
{
    /// <summary>Hazırlanan tüm değişiklikleri tek işlemde yazar. Çakışmada <see cref="RecordConflictException"/>.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IRecordStoreRegistry
{
    IRecordStore? Find(string entityKey);
}

public sealed class RecordStoreRegistry(IEnumerable<IRecordStore> stores) : IRecordStoreRegistry
{
    private readonly Dictionary<string, IRecordStore> _stores =
        stores.ToDictionary(s => s.EntityKey, StringComparer.OrdinalIgnoreCase);

    public IRecordStore? Find(string entityKey) => _stores.GetValueOrDefault(entityKey);
}
