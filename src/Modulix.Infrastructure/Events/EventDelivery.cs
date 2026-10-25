using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Events;

public enum EventDeliveryStatus
{
    /// <summary>Teslim edilmeyi bekliyor (ilk deneme, yeniden deneme zamanı gelmedi veya bir işleyicide kiralı).</summary>
    Pending = 1,

    Succeeded = 2,

    /// <summary>Kalıcı hata veya deneme hakkı bitti (dead-letter); platform yöneticisi yeniden dener veya atlar.</summary>
    Failed = 3,

    /// <summary>Teslim edilmedi: firma kapalı, aboneliğin modülü kapatıldı, abonelik kaldırıldı veya yönetici atladı.</summary>
    Skipped = 4,
}

/// <summary>
/// Bir olayın bir aboneye teslimi (platform.EventDeliveries, ADR-0007). Dağıtıcı olayı eşleşen her aboneliğe bir satır
/// olarak dağıtır; hata, yeniden deneme ve sıra abone başınadır. <c>(TenantId, Subscription, EventId)</c> tekildir: aynı
/// olay aynı aboneye ikinci kez satır açamaz (inbox). Tüm tenant'ları tarayan işleyici kullandığı için tablo platform
/// şemasında, RLS dışındadır; teslim olayın tenant'ının bağlamında yapılır. Payload burada tutulmaz (olaydadır).
/// </summary>
public sealed class EventDelivery
{
    public const int LastErrorMaxLength = OutboxMessage.LastErrorMaxLength;

    private EventDelivery()
    {
        // EF Core için
    }

    public EventDelivery(OutboxMessage message, string subscription, bool ordered, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);

        Id = Guid.CreateVersion7();
        EventId = message.Id;
        TenantId = message.TenantId;
        Subscription = subscription;
        EntityKey = message.EntityKey;
        RecordId = message.RecordId;
        Sequence = message.Sequence;
        Ordered = ordered && message.Sequence is not null;
        Status = EventDeliveryStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Aboneliğin anahtarı (ör. "core.assignment-email").</summary>
    public string Subscription { get; private set; } = string.Empty;

    // Sıra denetimi için olaydan kopyalanır.
    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    public long? Sequence { get; private set; }

    /// <summary>Aynı kaydın önceki teslimi bitmeden (başarılı veya atlanmış) bu teslim alınmaz.</summary>
    public bool Ordered { get; private set; }

    public EventDeliveryStatus Status { get; private set; }

    /// <summary>Başlamış denemeler; işleyici teslimi kiralarken artırır.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// Bekleyen teslim bu zamandan sonra alınır. Kiralayan işleyici bunu kira süresi kadar ileri atar ve abone çalışırken
    /// uzatır (heartbeat); işleyici çökerse kira bitince teslim başka işleyiciye geçer.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Son kiralamanın belirteci; teslimi bitiren güncelleme bunu şart koşar (geç kalan eski kiracı yazamaz).</summary>
    public Guid? LeaseToken { get; private set; }

    /// <summary>İstisna türü ve iletisi; payload veya alan değeri içermez.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }
}

internal sealed class EventDeliveryConfiguration : IEntityTypeConfiguration<EventDelivery>
{
    public void Configure(EntityTypeBuilder<EventDelivery> builder)
    {
        builder.ToTable("EventDeliveries", DatabaseSchemas.Platform);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Subscription).HasMaxLength(ModuleCatalog.SubscriptionKeyMaxLength).IsRequired();
        builder.Property(d => d.EntityKey).HasMaxLength(AuditLogEntry.EntityKeyMaxLength).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(EventDelivery.LastErrorMaxLength);

        // Inbox: aynı olay aynı aboneye bir kez.
        builder.HasIndex(d => new { d.TenantId, d.Subscription, d.EventId }).IsUnique();

        // Zamanı gelmiş teslimler; sıra denetimi (aynı abone, aynı kayıt, daha küçük sıra); platform ekranı ve temizlik.
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt });
        builder.HasIndex(d => new { d.TenantId, d.Subscription, d.EntityKey, d.RecordId, d.Sequence });
        builder.HasIndex(d => new { d.Status, d.CompletedAt });
        builder.HasIndex(d => d.EventId);

        // Olay, teslimleri bitip saklama süresi dolunca silinir; önce teslimler silinir.
        builder.HasOne<OutboxMessage>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
