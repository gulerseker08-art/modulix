using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Support.Domain;

// Destek modülü entity'leri (ADR-0024). Alanlar kayıt depolarından (Records) doğrulanarak yazılır. Müşteri ve kişiler ortak kayıttır
// (core.party / core.person); CRM tablolarına bağ yoktur, bu yüzden CRM kapalıyken de talep açılır.

/// <summary>Tüm destek kayıtlarının ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class SupportEntity : ITenantEntity, ICompanyShared
{
    protected SupportEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Oluşturma anı. Birleştirmede taşınan mesajın özgün zamanını korumak için modül içinden ayarlanabilir.</summary>
    public DateTimeOffset CreatedAt { get; internal set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri, JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Bir kullanıcıya atanabilen kayıt. Sorumlu, aktif tenant'ın etkin üyesi olmalıdır.</summary>
public interface IOwnedEntity
{
    Guid? OwnerId { get; }
}

/// <summary>
/// Destek kuyruğu: talebin bekleme yeri. Sahiplikten (sorumlu kullanıcı) ayrıdır: talep bir kuyrukta ve sorumlusuz olabilir (atanmamış),
/// ya da hem kuyrukta hem sorumlusu olabilir. Tenant başına tek varsayılan kuyruk vardır; yönlendirme eşleşmezse talep oraya düşer.
/// </summary>
public sealed class SupportQueue : SupportEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public bool IsDefault { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Talep kategorisi (tür–kategori–alt kategori hiyerarşisi: en çok iki düzey). Varsayılan kuyruğu yönlendirmede kullanılır. Kategori pasife
/// alınabilir; silinirse talepler kategorisiz kalmaz: kullanılmışsa silme reddedilir.
/// </summary>
public sealed class TicketCategory : SupportEntity
{
    public const int NameMaxLength = 100;

    public string Name { get; internal set; } = string.Empty;

    public Guid? ParentId { get; internal set; }

    /// <summary>Bu kategori altına açılan talebin varsayılan kuyruğu; yoksa tenant varsayılan kuyruğu.</summary>
    public Guid? DefaultQueueId { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public int Position { get; internal set; }

    /// <summary>Bu kategoriye açılan talepte zorunlu alanlar (virgülle; <see cref="Lifecycle.CategoryFieldRules.Allowed"/>). Üst kategorinin zorunlu alanları da geçerlidir.</summary>
    public string? RequiredFields { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Destek talebi, şikâyet, bilgi talebi ya da hizmet isteği (tür yönetilebilir seçim listesidir). Aktivite (görev/görüşme) değildir. Durum,
/// sorumlu, kuyruk, öncelik ve çözüm alanları yalnızca iş akışı eylemleriyle yazılır. Müşterisi henüz tanımlanmamış başvuru başvuran
/// bilgisiyle kaydedilir; sonradan ortak firmaya/kişiye bağlanır.
/// </summary>
public sealed class SupportTicket : SupportEntity, IOwnedEntity
{
    public const int NumberMaxLength = 30;
    public const int SubjectMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int KeyMaxLength = 40;
    public const int StatusMaxLength = 20;
    public const int ReasonMaxLength = 500;
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 254;
    public const int PhoneMaxLength = 30;
    public const int ClientKeyMaxLength = 100;

    /// <summary>Tenant içinde benzersiz numara (TK-20261101-AB12CD).</summary>
    public string Number { get; internal set; } = string.Empty;

    public string Subject { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Tür: <c>support.ticket-type</c> seçim listesinden (destek talebi, şikâyet, bilgi talebi, hizmet isteği...). Kategori anlamı kodun anladığı davranışı belirler.</summary>
    public string TypeKey { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.TicketChannels"/>.</summary>
    public string Channel { get; internal set; } = Lifecycle.TicketChannels.User;

    /// <summary>Müşteri: ortak taraf kaydı (core.party). Tanımsız başvuruda boştur.</summary>
    public Guid? PartyId { get; internal set; }

    public Guid? PersonId { get; internal set; }

    /// <summary>Müşteri (ya da yalnızca kişi) adının kopyası: listede ortak kayıt sorgulamadan gösterilir; bağlantı değişince yenilenir.</summary>
    public string? CustomerName { get; internal set; }

    // ---- Tanımsız başvuran (müşteri kaydı açmadan)

    public string? ReporterName { get; internal set; }

    public string? ReporterEmail { get; internal set; }

    public string? ReporterPhone { get; internal set; }

    public Guid? CategoryId { get; internal set; }

    // ---- Etki × aciliyet → öncelik

    public string Impact { get; internal set; } = Lifecycle.TicketLevel.Medium;

    public string Urgency { get; internal set; } = Lifecycle.TicketLevel.Medium;

    /// <summary>Etki × aciliyet matrisinden hesaplanır; yetkili ve gerekçeli elle değiştirilirse <see cref="PriorityOverridden"/> olur.</summary>
    public string Priority { get; internal set; } = Lifecycle.TicketPriority.Normal;

    public bool PriorityOverridden { get; internal set; }

    public string? PriorityReason { get; internal set; }

    // ---- Durum ve sahiplik

    public string Status { get; internal set; } = Lifecycle.TicketStatus.New;

    /// <summary>Son durum geçişinin gerekçesi / bekleme nedeni.</summary>
    public string? StatusReason { get; internal set; }

    public Guid? QueueId { get; internal set; }

    /// <summary>Sorumlu kullanıcı. Veri kapsamı bu alana göre uygulanır; boşsa talep atanmamıştır.</summary>
    public Guid? OwnerId { get; internal set; }

    /// <summary>Önceki/ilişkili talep (yeniden açma penceresi dışındaki yanıt ya da takip talebi).</summary>
    public Guid? ParentTicketId { get; internal set; }

    /// <summary>Birleştirilmişse hedef talep (durum <c>Merged</c>); kaynağın geçmişi, mesajları ve dış anahtarı kaynakta kalır.</summary>
    public Guid? MergedIntoId { get; internal set; }

    /// <summary>İstemcinin verdiği tekrar-koruma anahtarı: aynı anahtarla gelen ikinci oluşturma mükerrer kayıt üretmez.</summary>
    public string? ClientKey { get; internal set; }

    // ---- Şikâyet alanları

    public DateOnly? EventDate { get; internal set; }

    public string? ExpectedOutcome { get; internal set; }

    // ---- Çözüm

    public string? ResolutionCode { get; internal set; }

    public string? ResolutionNote { get; internal set; }

    public DateTimeOffset? ResolvedAt { get; internal set; }

    public Guid? ResolvedBy { get; internal set; }

    public string? RootCause { get; internal set; }

    public string? CorrectiveAction { get; internal set; }

    public DateTimeOffset? ClosedAt { get; internal set; }

    public int ReopenCount { get; internal set; }

    /// <summary>İlk insan yanıtının kabul edildiği an (otomatik mesaj ve iç not sayılmaz). SLA ölçümünün (dilim 2) girdisidir.</summary>
    public DateTimeOffset? FirstResponseAt { get; internal set; }

    /// <summary>Çalışan ilk yanıt SLA sayacının hedef anı (SLA örneğinden kopya; liste için). Duraklatılmış, tamamlanmış ya da hedefsiz ise boştur.</summary>
    public DateTimeOffset? FirstResponseDueAt { get; internal set; }

    /// <summary>Çalışan çözüm SLA sayacının hedef anı (SLA örneğinden kopya; liste için).</summary>
    public DateTimeOffset? ResolutionDueAt { get; internal set; }

    public DateTimeOffset LastActivityAt { get; internal set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Talep mesajı. <see cref="Visibility"/> müşteriye görünen yanıt (Public) ile iç notu (Internal) kesin ayırır; iç not hiçbir müşteri
/// çıktısına, bildirime ya da dışa aktarmaya girmez. Mesajlar değiştirilmez ve silinmez.
/// </summary>
public sealed class TicketMessage : SupportEntity
{
    public const int BodyMaxLength = 8000;

    public Guid TicketId { get; internal set; }

    /// <summary><see cref="Lifecycle.MessageVisibility"/>.</summary>
    public string Visibility { get; internal set; } = Lifecycle.MessageVisibility.Internal;

    /// <summary><see cref="Lifecycle.MessageDirection"/>.</summary>
    public string Direction { get; internal set; } = Lifecycle.MessageDirection.Note;

    public string Body { get; internal set; } = string.Empty;

    public Guid? AuthorId { get; internal set; }

    /// <summary>Başka bir talepten birleştirme ile kopyalandıysa kaynak talep (özgün zamanı korunur); yoksa boş.</summary>
    public Guid? SourceTicketId { get; internal set; }

    /// <summary><see cref="Lifecycle.DeliveryStatus"/>.</summary>
    public string DeliveryStatus { get; internal set; } = Lifecycle.DeliveryStatus.NotRequired;

    /// <summary>Otomatik alındı mesajı gibi sistem mesajı: ilk insan yanıtı sayılmaz.</summary>
    public bool IsAutomatic { get; internal set; }
}

/// <summary>Talep tarihçesi: durum, atama, kuyruk, öncelik, mesaj ve çözüm olayları. Gövde içermez (iç not içeriği tarihçeye sızmaz).</summary>
public sealed class TicketEvent : SupportEntity
{
    public Guid TicketId { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public Guid? FromOwnerId { get; internal set; }

    public Guid? ToOwnerId { get; internal set; }

    public Guid? FromQueueId { get; internal set; }

    public Guid? ToQueueId { get; internal set; }

    public string? Reason { get; internal set; }
}
