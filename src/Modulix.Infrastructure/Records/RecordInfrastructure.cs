using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
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
/// Outbox (platform.OutboxMessages). Olaylar kaydın kendisiyle aynı işlemde yazılır; işleyici (E12)
/// sonradan yayınlar. İşleyici tüm tenant'ları tarayacağı için tablo platform şemasındadır; her satır
/// TenantId taşır ve işleyici olayı o tenant'ın bağlamında işler.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        // EF Core için
    }

    public OutboxMessage(DomainEvent domainEvent)
    {
        Id = Guid.CreateVersion7();
        TenantId = domainEvent.TenantId;
        EventType = domainEvent.EventType;
        EntityKey = domainEvent.EntityKey;
        RecordId = domainEvent.RecordId;
        Payload = domainEvent.Payload.ToJsonString();
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    public string Payload { get; private set; } = "{}";

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int AttemptCount { get; private set; }

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
        builder.Property(o => o.EventType).HasMaxLength(100).IsRequired();
        builder.Property(o => o.EntityKey).HasMaxLength(AuditLogEntry.EntityKeyMaxLength).IsRequired();
        builder.Property(o => o.Payload).IsRequired();
        builder.Property(o => o.LastError).HasMaxLength(2000);
        builder.HasIndex(o => new { o.ProcessedAt, o.OccurredAt });
    }
}

internal sealed class EfAuditWriter(ModulixDbContext db) : IAuditWriter
{
    public void Add(AuditEntry entry) => db.AuditLog.Add(new AuditLogEntry(entry));
}

internal sealed class EfDomainEventOutbox(ModulixDbContext db) : IDomainEventOutbox
{
    public void Add(DomainEvent domainEvent) => db.OutboxMessages.Add(new OutboxMessage(domainEvent));
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
/// kullanıcının rollerinde var ve modülü açık mı? İzin istemeyen depo için aktif tenant yeterlidir.
/// </summary>
internal sealed class StorePermissionService(IRecordStoreRegistry stores, ICurrentAccess access) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken) =>
        stores.Find(entityKey)?.PermissionFor(operation) is not { } permission
        || await access.HasPermissionAsync(permission, cancellationToken);
}

public static class RecordsSetup
{
    /// <summary>
    /// Kayıt boru hattı ve Core'un kayıt türleri. Lisans adımı modül haklarını, yetki adımı rollerin izinlerini
    /// denetler (E4), veri kapsamı adımı rollerin kayıt kapsamını ve ekipleri uygular (Y8).
    /// </summary>
    public static IServiceCollection AddModulixRecords(this IServiceCollection services)
    {
        services.TryAddScoped<IEntitlementService, ModuleEntitlementService>();
        services.TryAddScoped<IPermissionService, StorePermissionService>();
        services.TryAddScoped<IDataScopeService, MembershipDataScope>();

        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IDomainEventOutbox, EfDomainEventOutbox>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRecordStoreRegistry, RecordStoreRegistry>();
        services.AddScoped<IRecordPipeline, RecordPipeline>();

        services.AddScoped<IRecordStore, TenantSettingRecordStore>();
        return services;
    }
}
