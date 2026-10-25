using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>Audit kaydı (core.AuditLog). Tenant verisidir: Row-Level Security ile korunur, uygulama değiştiremez veya silemez.</summary>
public sealed class AuditLogEntry : ITenantEntity
{
    public const int EntityKeyMaxLength = 100;
    public const int CorrelationIdMaxLength = 64;

    private AuditLogEntry()
    {
        // EF Core için
    }

    public AuditLogEntry(AuditEntry entry)
    {
        Id = Guid.CreateVersion7();
        TenantId = entry.TenantId;
        UserId = entry.UserId;
        EntityKey = entry.EntityKey;
        RecordId = entry.RecordId;
        Action = entry.Action;
        Changes = entry.Changes.ToJsonString();
        Source = entry.Source;
        CorrelationId = entry.CorrelationId;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    public AuditAction Action { get; private set; }

    /// <summary>JSON: { "alan": { "old": ..., "new": ... } }</summary>
    public string Changes { get; private set; } = "{}";

    public ExecutionSource Source { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}

/// <summary>
/// Outbox (platform.OutboxMessages), olay zarfı (ADR-0007). Olaylar kaydın kendisiyle aynı işlemde yazılır; dağıtıcı
/// (<c>EventDispatcher</c>) sonradan abonelere teslim satırı olarak dağıtır. Dağıtıcı tüm tenant'ları tarayacağı için tablo
/// platform şemasındadır; her satır TenantId taşır ve abone olayı o tenant'ın bağlamında işler.
/// </summary>
public sealed class OutboxMessage
{
    public const int EventTypeMaxLength = 100;
    public const int ReasonMaxLength = 50;
    public const int LastErrorMaxLength = 2000;

    private OutboxMessage()
    {
        // EF Core için
    }

    public OutboxMessage(DomainEvent domainEvent, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TenantId = domainEvent.TenantId;
        EventType = domainEvent.EventType;
        SchemaVersion = domainEvent.SchemaVersion;
        EntityKey = domainEvent.EntityKey;
        RecordId = domainEvent.RecordId;
        Sequence = domainEvent.Sequence;
        UserId = domainEvent.UserId;
        Source = domainEvent.Source;
        CorrelationId = domainEvent.CorrelationId is { Length: > AuditLogEntry.CorrelationIdMaxLength } correlation
            ? correlation[..AuditLogEntry.CorrelationIdMaxLength]
            : domainEvent.CorrelationId;
        CausationId = domainEvent.CausationId;
        Depth = domainEvent.Depth;
        Reason = domainEvent.Reason;
        Payload = domainEvent.Payload.ToJsonString();
        OccurredAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public int SchemaVersion { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    /// <summary>Kaydın değişiklikten sonraki sürümü; aynı kaydın olayları aboneye bu sırayla teslim edilir. Boşsa sırasız.</summary>
    public long? Sequence { get; private set; }

    /// <summary>İşlemi yapan kişi; abonenin değişikliğinde boş (sistem).</summary>
    public Guid UserId { get; private set; }

    public ExecutionSource Source { get; private set; }

    public string? CorrelationId { get; private set; }

    public Guid? CausationId { get; private set; }

    public int Depth { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>JSON: { "alan": { "old": ..., "new": ... } }. Hassas alanlar dahil tam fark; süzgeç teslimde uygulanır.</summary>
    public string Payload { get; private set; } = "{}";

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Abonelere dağıtıldığı (teslim satırlarının açıldığı) zaman; boşsa dağıtılmayı bekler.</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>Dağıtımla ilgili not (ör. derinlik sınırı aşıldı, dağıtılmadı). Payload veya alan değeri içermez.</summary>
    public string? LastError { get; private set; }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("AuditLog", DatabaseSchemas.Core);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.EntityKey).HasMaxLength(AuditLogEntry.EntityKeyMaxLength).IsRequired();
        builder.Property(a => a.Changes).IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(AuditLogEntry.CorrelationIdMaxLength);
        builder.HasIndex(a => new { a.TenantId, a.EntityKey, a.RecordId, a.OccurredAt });

        // Denetim kaydı ekranı (Y5): en yeni üstte, kullanıcıya göre filtre.
        builder.HasIndex(a => new { a.TenantId, a.OccurredAt });
        builder.HasIndex(a => new { a.TenantId, a.UserId, a.OccurredAt });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", DatabaseSchemas.Platform);
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.EventType).HasMaxLength(OutboxMessage.EventTypeMaxLength).IsRequired();
        builder.Property(o => o.SchemaVersion).HasDefaultValue(1);
        builder.Property(o => o.EntityKey).HasMaxLength(AuditLogEntry.EntityKeyMaxLength).IsRequired();
        builder.Property(o => o.Source).HasDefaultValue(ExecutionSource.Api);
        builder.Property(o => o.CorrelationId).HasMaxLength(AuditLogEntry.CorrelationIdMaxLength);
        builder.Property(o => o.Reason).HasMaxLength(OutboxMessage.ReasonMaxLength);
        builder.Property(o => o.Payload).IsRequired();
        builder.Property(o => o.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // Dağıtıcı dağıtılmamış olayları yazılış sırasıyla okur; temizlik dağıtım zamanına bakar.
        builder.HasIndex(o => new { o.DispatchedAt, o.OccurredAt });
    }
}

internal sealed class EfAuditWriter(ModulixDbContext db) : IAuditWriter
{
    public void Add(AuditEntry entry) => db.AuditLog.Add(new AuditLogEntry(entry));
}

internal sealed class EfDomainEventOutbox(ModulixDbContext db, TimeProvider time) : IDomainEventOutbox
{
    public void Add(DomainEvent domainEvent) => db.OutboxMessages.Add(new OutboxMessage(domainEvent, time.GetUtcNow()));
}

internal sealed class EfUnitOfWork(ModulixDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RecordConflictException("Kayıt siz düzenlerken değişti. Güncel halini alıp tekrar deneyin.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new RecordConflictException("Aynı anahtarla bir kayıt zaten var.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 })
        {
            // Yabancı anahtar: silinmek istenen kayda bağlı başka kayıtlar var.
            throw new RecordConflictException("Bu kayda bağlı başka kayıtlar olduğu için işlem yapılamadı.", exception);
        }
    }
}

/// <summary>
/// Boru hattının lisans adımı: özelliğin modülü (FeatureKey'in ilk bölümü, ör. "crm.company" → "crm")
/// aktif tenant için açık mı? Core özellikleri her zaman açıktır. Plan bazlı özellik hakları E5'te gelir.
/// </summary>
internal sealed class ModuleEntitlementService(ITenantContextAccessor tenantAccessor, ICurrentAccess access) : IEntitlementService
{
    public async Task<bool> CanUseAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) =>
        tenantAccessor.Current?.TenantId == tenantId
        && await access.IsModuleEnabledAsync(AccessKeys.ModuleOf(featureKey), cancellationToken);
}

/// <summary>
/// Boru hattının yetki adımı: kayıt deposunun işlem için istediği izin (<see cref="IRecordStore.PermissionFor"/>)
/// kullanıcının rollerinde var ve modülü açık mı? İzin istemeyen depo için aktif tenant yeterlidir. Depo birden fazla
/// izin sayıyorsa (<see cref="IRecordStore.AnyPermissionFor"/>) biri yeterlidir.
/// </summary>
internal sealed class StorePermissionService(IRecordStoreRegistry stores, ICurrentAccess access) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken)
    {
        var store = stores.Find(entityKey);
        if (store?.AnyPermissionFor(operation) is { } any)
        {
            var granted = await access.GetPermissionsAsync(cancellationToken);
            return any.Any(granted.Contains);
        }

        return store?.PermissionFor(operation) is not { } permission
            || await access.HasPermissionAsync(permission, cancellationToken);
    }
}

public static class RecordsSetup
{
    /// <summary>
    /// Kayıt boru hattı ve Core'un kayıt türleri. Lisans adımı modül haklarını, yetki adımı rollerin izinlerini
    /// denetler (E4), veri kapsamı adımı rollerin kayıt kapsamını ve ekipleri uygular (Y8), hassas alanlar izinlere göre gizlenir (Y8b).
    /// </summary>
    public static IServiceCollection AddModulixRecords(this IServiceCollection services)
    {
        services.TryAddScoped<IEntitlementService, ModuleEntitlementService>();
        services.TryAddScoped<IPermissionService, StorePermissionService>();
        services.TryAddScoped<IDataScopeService, MembershipDataScope>();
        services.TryAddScoped<IFieldAccessService, PermissionFieldAccess>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<EventHandlingScope>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IDomainEventOutbox, EfDomainEventOutbox>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRecordStoreRegistry, RecordStoreRegistry>();
        services.AddScoped<IRecordPipeline, RecordPipeline>();

        services.AddScoped<IRecordStore, TenantSettingRecordStore>();
        services.AddScoped<IRecordStore, PartyRecordStore>();
        services.AddScoped<IRecordStore, PersonRecordStore>();
        return services;
    }
}
