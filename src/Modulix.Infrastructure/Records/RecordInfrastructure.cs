using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
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
    }
}

public static class RecordsSetup
{
    /// <summary>
    /// Kayıt boru hattı ve Core'un kayıt türleri. Lisans, yetki ve veri kapsamı için v0 varsayılanları
    /// TryAdd ile kaydedilir; E4 ve E5'te gerçek uygulamalar bunların yerine geçer.
    /// </summary>
    public static IServiceCollection AddModulixRecords(this IServiceCollection services)
    {
        services.TryAddScoped<IEntitlementService, AllowAllEntitlements>();
        services.TryAddScoped<IPermissionService, AllowAllPermissions>();
        services.TryAddScoped<IDataScopeService, TenantWideDataScope>();

        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IDomainEventOutbox, EfDomainEventOutbox>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IRecordStoreRegistry, RecordStoreRegistry>();
        services.AddScoped<IRecordPipeline, RecordPipeline>();

        services.AddScoped<IRecordStore, TenantSettingRecordStore>();
        return services;
    }
}
