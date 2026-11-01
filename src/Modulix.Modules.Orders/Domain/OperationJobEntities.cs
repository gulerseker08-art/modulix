namespace Modulix.Modules.Orders.Domain;

/// <summary>
/// Operasyon işi: siparişin kalemlerine bağlı, sorumlusu, ekibi, tarihleri, görevleri ve bağımlılıkları olan gerçek iş kaydı. Kabul edilen devirden
/// ayarlanan stratejiye göre (türe göre, tek iş ya da kalem başına) bir kez üretilir; kullanıcı da elle iş açabilir. Atanacak sorumlu/ekip yoksa iş
/// kaybolmaz, "Atama bekliyor" durumunda görünür. Bağımlı iş tamamlanmadan başlatılamaz (sunucuda uygulanır).
/// </summary>
public sealed class OperationJob : OrdersEntity
{
    public const int NumberMaxLength = 30;
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int KeyMaxLength = 60;

    public Guid OrderId { get; internal set; }

    /// <summary>İşi üreten devir kaydı; elle açılan işte de kabul edilmiş devir bağlanır.</summary>
    public Guid? HandoverId { get; internal set; }

    /// <summary>Tenant içinde benzersiz numara (OP-20261101-AB12CD).</summary>
    public string Number { get; internal set; } = string.Empty;

    public string Title { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.JobKind"/>: Delivery, Service, Mixed ya da Other.</summary>
    public string Kind { get; internal set; } = Lifecycle.JobKind.Other;

    /// <summary>
    /// Üretim grubu anahtarı (<c>kind:Product</c>, <c>kind:Service</c>, <c>all</c>, <c>line:{id}</c>). Aynı siparişte aynı grup için açık iş yalnızca bir kez
    /// bulunur (filtreli benzersiz indeks): yeniden deneme ya da tekrar kabul mükerrer iş üretmez. Elle açılan işte boştur.
    /// </summary>
    public string? GroupKey { get; internal set; }

    /// <summary><see cref="Lifecycle.JobStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.JobStatus.AwaitingAssignment;

    /// <summary><see cref="Lifecycle.HandoverPriority"/> değerleri.</summary>
    public string Priority { get; internal set; } = Lifecycle.HandoverPriority.Normal;

    public Guid? ResponsibleUserId { get; internal set; }

    /// <summary>Sorumlu ekip (serbest metin; ekip lookup'ı yoktur).</summary>
    public string? TeamName { get; internal set; }

    public DateOnly? PlannedStartDate { get; internal set; }

    public DateOnly? PlannedEndDate { get; internal set; }

    public DateTimeOffset? ActualStartAt { get; internal set; }

    public DateTimeOffset? ActualEndAt { get; internal set; }

    public string? Location { get; internal set; }

    public string? Notes { get; internal set; }

    public string? CancelReason { get; internal set; }

    /// <summary><c>Handover</c> (devir kabulünden üretildi) ya da <c>Manual</c>.</summary>
    public string Source { get; internal set; } = "Handover";

    /// <summary>Atama beklerken takip aktivitesi üretildi mi (iş başına bir kez).</summary>
    public DateTimeOffset? UnassignedAlertAt { get; internal set; }

    /// <summary>Planlanan bitiş geçtiğinde takip aktivitesi üretildi mi (iş başına bir kez).</summary>
    public DateTimeOffset? OverdueAlertAt { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>İşin bağlı olduğu sipariş kalemi. Bir kalem aynı anda yalnızca bir açık işte bulunur.</summary>
public sealed class OperationJobLine : OrdersEntity
{
    public Guid JobId { get; internal set; }

    public Guid OrderId { get; internal set; }

    public Guid OrderLineId { get; internal set; }
}

/// <summary>İş görevi / kontrol listesi maddesi. Zorunlu görevler tamamlanmadan iş tamamlanamaz.</summary>
public sealed class OperationJobTask : OrdersEntity
{
    public const int TitleMaxLength = 300;

    public Guid JobId { get; internal set; }

    public int Position { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    public bool IsRequired { get; internal set; } = true;

    public bool IsDone { get; internal set; }

    public Guid? DoneBy { get; internal set; }

    public DateTimeOffset? DoneAt { get; internal set; }

    public string? Note { get; internal set; }

    /// <summary>Ürün/hizmet türü şablonundan gelen görev (silinmez); kullanıcı görevi silinebilir.</summary>
    public bool IsDefault { get; internal set; }
}

/// <summary><see cref="JobId"/> işi, <see cref="DependsOnJobId"/> işi tamamlanmadan başlayamaz.</summary>
public sealed class OperationJobDependency : OrdersEntity
{
    public Guid JobId { get; internal set; }

    public Guid DependsOnJobId { get; internal set; }

    public Guid OrderId { get; internal set; }
}

/// <summary>İş geçmişi: atama, başlatma, tamamlama, iptal ve üretim. Eklenir, değişmez.</summary>
public sealed class OperationJobEvent : OrdersEntity
{
    public Guid JobId { get; internal set; }

    public Guid OrderId { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public string? Note { get; internal set; }
}
