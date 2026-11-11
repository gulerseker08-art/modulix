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
