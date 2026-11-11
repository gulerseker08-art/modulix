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
