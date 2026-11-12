using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Logistics.Domain;

/// <summary>
/// Lojistik tablolarının ortak tabanı. Tenant verisidir (<see cref="ITenantEntity"/>): sorgu filtresi ve Row-Level Security uygulanır.
/// Sürücü ve taşıyıcı koşulu şirketler arasında ortaktır (<see cref="ICompanyShared"/>, ADR-0038 bölüm 3); talep, sevkiyat ve sefer
/// şirket kapsamlı olarak sonraki dilimlerde gelir.
/// </summary>
public abstract class LogisticsEntity : ITenantEntity, ICompanyShared
{
    protected LogisticsEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>
/// Sürücü kartı (ADR-0038 K2, dilim 3): ayrı bir kişi tablosu değildir; <c>core.Person</c> kaydının lojistik uzantısıdır (ad, telefon ve
/// e-posta kişide). Kadrolu ya da taşeron olur; taşeron sürücüde taşıyıcı firma ("Taşıyıcı" ilişki rolü) zorunludur. Varsayılan araç
/// Filo'dan seçilir (<c>IVehicleDirectory</c>; yalnızca motorlu araç, Filo kapalıysa seçilemez). Yetkinlikler (ehliyet, SRC,
/// psikoteknik) süreli belgedir ve Core belge yeteneğiyle (dilim 2) gelir. Sürücü silinmez, pasife alınır (K12).
/// </summary>
public sealed class Driver : LogisticsEntity
{
    public const int NotesMaxLength = 2000;

    public static class Types
    {
        public const string Employee = "employee";
        public const string Subcontracted = "subcontracted";

        public static IReadOnlyList<string> All { get; } = [Employee, Subcontracted];
    }

    public static class Statuses
    {
        public const string Active = "active";
        public const string OnLeave = "onLeave";
        public const string Passive = "passive";

        public static IReadOnlyList<string> All { get; } = [Active, OnLeave, Passive];
    }

    /// <summary>Sürücünün kişi kaydı; tenant içinde bir kişinin tek sürücü kartı olur.</summary>
    public Guid PersonId { get; internal set; }

    public string DriverType { get; internal set; } = Types.Employee;

    /// <summary>Taşeron sürücünün çalıştığı taşıyıcı firma; kadrolu sürücüde boştur.</summary>
    public Guid? CarrierPartyId { get; internal set; }

    /// <summary>Genellikle kullandığı araç (Filo); sefer atamasında öneri olarak kullanılır.</summary>
    public Guid? DefaultVehicleId { get; internal set; }

    public string Status { get; internal set; } = Statuses.Active;

    public string? Notes { get; internal set; }
}

/// <summary>
/// Taşıyıcı anlaşması (ADR-0038 K4, dilim 3b): taşeron taşıyıcıyla yapılan çalışma koşulu. Taşıyıcının kimliği <c>core.Party</c>'dir
/// ("Taşıyıcı" ilişki rolü); bu kayıt yalnızca ilişkinin koşulunu (geçerlilik, para birimi, ödeme vadesi, ücret açıklaması, performans
/// notu) tutar. Aynı taşıyıcının etkin anlaşmalarının geçerlilik aralıkları çakışamaz: bir günde tek geçerli anlaşma olur. Tarife ve
/// hakediş hesabı sonraki dilimlerdedir (Ç18a.2, dilim 7); ücret bugün açıklama olarak girilir.
/// </summary>
public sealed class CarrierTerms : LogisticsEntity
{
    public const int TitleMaxLength = 200;
    public const int AgreementNoMaxLength = 50;
    public const int TextMaxLength = 2000;
    public const int MaxPaymentTermDays = 365;

    public Guid CarrierPartyId { get; internal set; }

    /// <summary>Anlaşmanın adı (ör. "2026 yurt içi parsiyel").</summary>
    public string Title { get; internal set; } = string.Empty;

    /// <summary>Sözleşme / anlaşma numarası; verilirse taşıyıcı içinde benzersiz.</summary>
    public string? AgreementNo { get; internal set; }

    public DateOnly ValidFrom { get; internal set; }

    /// <summary>Boşsa süresiz.</summary>
    public DateOnly? ValidTo { get; internal set; }

    /// <summary>ISO 4217 para birimi (ör. TRY, EUR).</summary>
    public string Currency { get; internal set; } = "TRY";

    /// <summary>Hakediş ödeme vadesi (gün).</summary>
    public int? PaymentTermDays { get; internal set; }

    /// <summary>Ücret anlaşmasının açıklaması (tarife altyapısı gelene kadar serbest metin).</summary>
    public string? RateNote { get; internal set; }

    /// <summary>Performans ve kalite notu (gecikme, hasar, iletişim).</summary>
    public string? PerformanceNote { get; internal set; }

    /// <summary>Pasif anlaşma çakışma denetimine girmez ve "geçerli" sayılmaz.</summary>
    public bool IsActive { get; internal set; } = true;
}

/// <summary>
/// Taşıma talebi ve sevkiyatın ortak yük ve güzergâh alanları (ADR-0038 K5, K6): gönderen ve alıcı firma, yükleme ve boşaltma yeri, tarih penceresi,
/// yük tanımı ve ölçüleri, taşıma türü (Y9b; kod yalnızca kategoriye bakar: komple / parsiyel). Adres bugün metindir; durak adresi lookup'ı (ADR-0016)
/// sefer diliminde gelir.
/// </summary>
public abstract class CargoEntity : LogisticsEntity
{
    public const int CityMaxLength = 100;
    public const int AddressMaxLength = 500;
    public const int TextMaxLength = 2000;
    public const int ReasonMaxLength = 500;

    /// <summary>Taşımayı isteyen ve navlunu ödeyen müşteri firma.</summary>
    public Guid CustomerPartyId { get; internal set; }

    public Guid? SenderPartyId { get; internal set; }

    public Guid? ReceiverPartyId { get; internal set; }

    public string TransportModeKey { get; internal set; } = string.Empty;

    public string PickupCity { get; internal set; } = string.Empty;

    public string? PickupAddress { get; internal set; }

    public string DeliveryCity { get; internal set; } = string.Empty;

    public string? DeliveryAddress { get; internal set; }

    /// <summary>En erken yükleme günü.</summary>
    public DateOnly PickupDate { get; internal set; }

    /// <summary>En geç teslim günü; boşsa belirtilmedi.</summary>
    public DateOnly? DeliveryBy { get; internal set; }

    public string CargoDescription { get; internal set; } = string.Empty;

    public decimal? WeightKg { get; internal set; }

    public decimal? VolumeM3 { get; internal set; }

    public int? PalletCount { get; internal set; }

    public decimal? LoadingMeters { get; internal set; }

    /// <summary>Tehlikeli madde (ADR) yükü; araç ve sürücü uygunluğu sefer atamasında denetlenir (dilim 5).</summary>
    public bool Hazardous { get; internal set; }

    public string? Notes { get; internal set; }

    /// <summary>Durum yalnızca iş akışı komutuyla değişir (K7); genel kayıt güncellemesi yazamaz.</summary>
    public string Status { get; internal set; } = string.Empty;

    /// <summary>Son geçişin gerekçesi (ret, iptal).</summary>
    public string? StatusReason { get; internal set; }
}

/// <summary>
/// Taşıma talebi (ADR-0038 K6): müşterinin taşıma isteği; elle ya da API'den açılır (web ve sipariş köprüsü sonraki dilimlerde). Onay iş akışından
/// geçer: Yeni → Onaylandı (aynı işlemde sevkiyat açılır) / Reddedildi (gerekçeli); yeni talep iptal edilebilir. Silinmez (K12).
/// </summary>
public sealed class TransportRequest : CargoEntity
{
    public const int ExternalRefMaxLength = 100;

    public static class Statuses
    {
        public const string New = "new";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } = [New, Approved, Rejected, Cancelled];
    }

    public static class Sources
    {
        public const string Manual = "manual";
        public const string Api = "api";

        public static IReadOnlyList<string> All { get; } = [Manual, Api];
    }

    public TransportRequest() => Status = Statuses.New;

    public string Source { get; internal set; } = Sources.Manual;

    /// <summary>Müşterinin ya da kaynak sistemin referansı; verilirse kaynak içinde tekildir (aynı talep iki kez açılmaz).</summary>
    public string? ExternalRef { get; internal set; }

    /// <summary>Onaylanınca açılan sevkiyat.</summary>
    public Guid? ShipmentId { get; internal set; }
}

/// <summary>
/// Sevkiyat (ADR-0038 K5, K7): onaylanan talepten doğan müşteri yükü. Durumlar iş akışı komutuyla ilerler: Planlama bekliyor → Planlandı →
/// Yükleniyor → Yolda; teslim (Teslim edildi / Kısmi / Başarısız) yalnızca teslim komutuyla gelir (dilim 6). İptal teslimden önce ve gerekçeyle.
/// Sefer ve durak bağı dilim 5'tedir. Silinmez (K12).
/// </summary>
public sealed class Shipment : CargoEntity
{
    public static class Statuses
    {
        public const string Pending = "pending";
        public const string Planned = "planned";
        public const string Loading = "loading";
        public const string InTransit = "inTransit";
        public const string Delivered = "delivered";
        public const string Partial = "partial";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } = [Pending, Planned, Loading, InTransit, Delivered, Partial, Failed, Cancelled];
    }

    public Shipment() => Status = Statuses.Pending;

    /// <summary>Kaynak taşıma talebi; her talepten en çok bir sevkiyat.</summary>
    public Guid RequestId { get; internal set; }
}
