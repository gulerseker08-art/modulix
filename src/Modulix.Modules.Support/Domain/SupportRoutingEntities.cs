namespace Modulix.Modules.Support.Domain;

// Dilim 3 entity'leri (ADR-0024): yönetilebilir öncelik matrisi, yönlendirme kuralları ve talep alt işleri.

/// <summary>Tenant'ın etki × aciliyet → öncelik hücresi. Tanımlanmamış hücre yerleşik matristen alınır; hücre başına tek kayıt vardır.</summary>
public sealed class PriorityMatrixEntry : SupportEntity
{
    public string Impact { get; internal set; } = Lifecycle.TicketLevel.Medium;

    public string Urgency { get; internal set; } = Lifecycle.TicketLevel.Medium;

    public string Priority { get; internal set; } = Lifecycle.TicketPriority.Normal;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Yönlendirme kuralı: kanal, tür, kategori, öncelik ve müşteri kısıtlarına uyan yeni talebi kuyruğa ve/veya sorumluya yönlendirir. Öncelik sıralıdır (<see cref="Order"/> küçük olan önce),
/// ilk eşleşme kazanır ve yalnızca talep açılırken bir kez değerlendirilir (döngü yoktur). Her kuralın en az bir eylemi (kuyruk ya da sorumlu) vardır.
/// </summary>
public sealed class TicketRoutingRule : SupportEntity
{
    public const int NameMaxLength = 100;

    public string Name { get; internal set; } = string.Empty;

    public int Order { get; internal set; } = 100;

    public bool IsActive { get; internal set; } = true;

    public string? Channel { get; internal set; }

    public string? TypeKey { get; internal set; }

    public Guid? CategoryId { get; internal set; }

    public string? Priority { get; internal set; }

    public Guid? PartyId { get; internal set; }

    public Guid? QueueId { get; internal set; }

    /// <summary>Atanacak sorumlu kullanıcı (etkin üye olmalı; değerlendirme anında pasifse atanmaz, kuyruk kuralı sürer).</summary>
    public Guid? OwnerId { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Talebin alt işi. Engelleyici (<see cref="IsBlocking"/>) açık alt iş varken talep çözülemez. Alt işler silinmez; yalnızca tamamlanır ya da yeniden açılır. Takip görevi/görüşme ise
/// aktivite altyapısıyla talebe bağlanır (alt iş onun yerine geçmez).
/// </summary>
public sealed class TicketTask : SupportEntity
{
    public const int TitleMaxLength = 200;

    public Guid TicketId { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    public Guid? AssigneeId { get; internal set; }

    public bool IsBlocking { get; internal set; }

    public bool IsDone { get; internal set; }

    public DateTimeOffset? DoneAt { get; internal set; }

    public Guid? DoneBy { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}
