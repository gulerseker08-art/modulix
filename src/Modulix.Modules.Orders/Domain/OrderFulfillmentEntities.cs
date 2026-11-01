namespace Modulix.Modules.Orders.Domain;

/// <summary>
/// Teslimat (ürün kalemleri): planlanan ve gerçekleşen tarih, kalem bazında planlanan ve teslim edilen miktar, teslim alan kişi, teslim kanıtı. Bir sipariş
/// birden çok ve kısmi teslimatla karşılanabilir; kalemin toplam teslim edilen miktarı etkin miktarı aşamaz (uygulamada ve <c>CHECK</c> kısıtıyla). Başarısız
/// teslimat gerekçeyle işaretlenir ve yeniden planlanır (yeni teslimat önceki kayda bağlanır); teslim edilen kayıtlar silinmez.
/// </summary>
public sealed class OrderDelivery : OrdersEntity
{
    public const int NumberMaxLength = 30;
    public const int TextMaxLength = 2000;

    public Guid OrderId { get; internal set; }

    /// <summary>İlgili operasyon işi (isteğe bağlı).</summary>
    public Guid? JobId { get; internal set; }

    /// <summary>Tenant içinde benzersiz numara (TS-20261101-AB12CD).</summary>
    public string Number { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.DeliveryStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.DeliveryStatus.Planned;

    public DateOnly PlannedDate { get; internal set; }

    public DateOnly? ActualDate { get; internal set; }

    public string? ReceiverName { get; internal set; }

    public string? ReceiverNote { get; internal set; }

    public string? FailureReason { get; internal set; }

    /// <summary>Bu teslimat başarısız bir teslimatın yeniden planlamasıysa önceki teslimat.</summary>
    public Guid? RescheduledFromId { get; internal set; }

    public string? RescheduleReason { get; internal set; }

    public string? Notes { get; internal set; }

    public DateTimeOffset? DeliveredAt { get; internal set; }

    public Guid? DeliveredBy { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>Teslimatın kalem satırı: planlanan miktar ve (teslim edildiyse) gerçekleşen miktar.</summary>
public sealed class OrderDeliveryLine : OrdersEntity
{
    public Guid DeliveryId { get; internal set; }

    public Guid OrderId { get; internal set; }

    public Guid OrderLineId { get; internal set; }

    public decimal PlannedQuantity { get; internal set; }

    public decimal DeliveredQuantity { get; internal set; }
}

/// <summary>
/// Teslim kanıtı (imzalı tutanak, fotoğraf): küçük, sınırlı ve tenant'a bağlı kayıt. Genel dosya deposu (Ç4) olmadığından içerik bu tabloda tutulur; boyut ve tür
/// sınırlıdır, içerik özeti (SHA-256) saklanır, indirme yetki ve kapsam denetiminden geçer ve her zaman ek olarak sunulur. İçerik günlüklere yazılmaz.
/// </summary>
public sealed class OrderDeliveryEvidence : OrdersEntity
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxPerDelivery = 10;

    public Guid DeliveryId { get; internal set; }

    public Guid OrderId { get; internal set; }

    public string FileName { get; internal set; } = string.Empty;

    public string ContentType { get; internal set; } = string.Empty;

    public int SizeBytes { get; internal set; }

    public string Sha256 { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public byte[] Content { get; internal set; } = [];

    public Guid? UploadedBy { get; internal set; }
}

/// <summary>
/// Hizmet kalemi başlangıç kaydı: planlanan ve gerçekleşen başlangıç, başlatan kullanıcı, müşteri başlangıç onayı, gecikme nedeni ve hizmetin tamamlanması. Hizmetin
/// <b>başlaması</b> ile <b>tamamlanması</b> ayrı olaylardır: başlatılan hizmet "Başladı" olur; süreli (tekrarlayan) hizmette devam eden yükümlülük, tamamlama kaydı
/// girilene kadar görünür kalır.
/// </summary>
public sealed class OrderServiceStart : OrdersEntity
{
    public Guid OrderId { get; internal set; }

    /// <summary>Hizmet kalemi; kalem başına tek kayıt vardır.</summary>
    public Guid OrderLineId { get; internal set; }

    /// <summary><see cref="Lifecycle.ServiceStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.ServiceStatus.Planned;

    public DateOnly? PlannedStartDate { get; internal set; }

    public DateTimeOffset? ActualStartAt { get; internal set; }

    public Guid? StartedBy { get; internal set; }

    public DateTimeOffset? CustomerConfirmedAt { get; internal set; }

    /// <summary>Başlangıcı onaylayan müşteri tarafı kişi (serbest metin).</summary>
    public string? CustomerConfirmedBy { get; internal set; }

    public string? ConfirmationNote { get; internal set; }

    /// <summary>Planlanan başlangıç gecikmişse ya da ertelenmişse nedeni.</summary>
    public string? DelayReason { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public Guid? CompletedBy { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}
