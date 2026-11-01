namespace Modulix.Modules.Orders.Domain;

/// <summary>
/// Satış devri kaydı: satış sorumlusunun operasyon ekibine yapılandırılmış devri (sipariş numarası göndermekten fazlası). Siparişin satış devri
/// durumu (<see cref="SalesOrder.HandoverStatus"/>) bu kaydın durumunun aynasıdır. Sipariş başına tek kayıt vardır; gönderilen devir operasyon
/// kabul edene ya da bilgi isteyene kadar düzenlenemez, kabul edilen devir değişmez (sonraki değişiklikler sipariş değişiklik talebiyle yürür).
/// </summary>
public sealed class OrderHandover : OrdersEntity
{
    public const int TextMaxLength = 4000;
    public const int ShortMaxLength = 300;

    public Guid OrderId { get; internal set; }

    /// <summary><see cref="Lifecycle.HandoverStatus"/>: Preparing, Ready, Sent, InfoRequested ya da Accepted.</summary>
    public string Status { get; internal set; } = Lifecycle.HandoverStatus.Preparing;

    /// <summary>Kaçıncı gönderim (ilk gönderimde 1; bilgi isteği sonrası yeniden gönderimde artar).</summary>
    public int Round { get; internal set; }

    public string? Scope { get; internal set; }

    public string? OutOfScope { get; internal set; }

    public string? Deliverables { get; internal set; }

    public string? Commitments { get; internal set; }

    public string? Location { get; internal set; }

    public string? ContactName { get; internal set; }

    public string? ContactPhone { get; internal set; }

    public string? ContactEmail { get; internal set; }

    public DateOnly? TargetStartDate { get; internal set; }

    public DateOnly? TargetEndDate { get; internal set; }

    /// <summary><see cref="Lifecycle.HandoverPriority"/>.</summary>
    public string Priority { get; internal set; } = Lifecycle.HandoverPriority.Normal;

    public string? RequiredDocuments { get; internal set; }

    public string? CustomerPrerequisites { get; internal set; }

    /// <summary>Mevcutsa ödeme ve sözleşme onayı koşulları (serbest metin).</summary>
    public string? PaymentConditions { get; internal set; }

    public string? Risks { get; internal set; }

    public string? SpecialRequests { get; internal set; }

    public string? Dependencies { get; internal set; }

    /// <summary>Sorumlu operasyon kullanıcısı (gönderirken önerilir, kabulde atanır).</summary>
    public Guid? ResponsibleUserId { get; internal set; }

    /// <summary>Sorumlu operasyon ekibi (serbest metin; ekip lookup'ı yoktur).</summary>
    public string? ResponsibleTeam { get; internal set; }

    public string? CompletionCriteria { get; internal set; }

    public string? AcceptanceCriteria { get; internal set; }

    /// <summary>Operasyonun son bilgi isteği.</summary>
    public string? InfoRequest { get; internal set; }

    public DateTimeOffset? SentAt { get; internal set; }

    public Guid? SentBy { get; internal set; }

    public DateTimeOffset? AcceptedAt { get; internal set; }

    public Guid? AcceptedBy { get; internal set; }

    /// <summary>Gönderimde operasyon için açılan takip aktivitesi (bu tur).</summary>
    public Guid? OperationsActivityId { get; internal set; }

    /// <summary>Bilgi isteğinde satış için açılan takip aktivitesi (bu tur).</summary>
    public Guid? SalesActivityId { get; internal set; }

    /// <summary>Kabul beklenirken gecikme takibi üretilen en son tur; aynı tur için tekrar üretilmez.</summary>
    public int EscalatedRound { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>Devir kontrol listesi maddesi. Zorunlu maddeler tamamlanmadan devir gönderilemez.</summary>
public sealed class OrderHandoverItem : OrdersEntity
{
    public const int TextMaxLength = 300;

    public Guid HandoverId { get; internal set; }

    public int Position { get; internal set; }

    public string Text { get; internal set; } = string.Empty;

    public bool IsRequired { get; internal set; } = true;

    public bool IsDone { get; internal set; }

    public Guid? DoneBy { get; internal set; }

    public DateTimeOffset? DoneAt { get; internal set; }

    public string? Note { get; internal set; }

    /// <summary>Sistemin varsayılan olarak açtığı madde (kullanıcı eklediği maddeden ayrılır).</summary>
    public bool IsDefault { get; internal set; }
}

/// <summary>Devir geçmişi: gönderen, kabul eden, tarihler, bilgi talepleri ve yeniden gönderimler. Eklenir, değişmez.</summary>
public sealed class OrderHandoverEvent : OrdersEntity
{
    public Guid HandoverId { get; internal set; }

    public Guid OrderId { get; internal set; }

    public int Round { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? Note { get; internal set; }
}
