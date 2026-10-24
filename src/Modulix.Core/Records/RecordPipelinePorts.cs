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

public interface IDataScopeService
{
    Task<bool> CanAccessAsync(TenantContext tenant, RecordData record, RecordOperation operation, CancellationToken cancellationToken);
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
    public Task<bool> CanAccessAsync(TenantContext tenant, RecordData record, RecordOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(true);
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
    string? CorrelationId);

public sealed record DomainEvent(Guid TenantId, string EventType, string EntityKey, Guid RecordId, JsonObject Payload);

public static class DomainEventTypes
{
    public const string RecordCreated = "RecordCreated";
    public const string RecordUpdated = "RecordUpdated";
    public const string RecordDeleted = "RecordDeleted";
    public const string StatusChanged = "StatusChanged";
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
