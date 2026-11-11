using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Fleet.Domain;

/// <summary>
/// Filo tablolarının ortak tabanı. Tenant verisidir (<see cref="ITenantEntity"/>): sorgu filtresi ve Row-Level Security uygulanır.
/// Araç kayıtları şirketler arasında ortaktır (<see cref="ICompanyShared"/>, ADR-0008 ve ADR-0038 bölüm 3).
/// </summary>
public abstract class FleetEntity : ITenantEntity, ICompanyShared
{
    protected FleetEntity()
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
/// Araç (ADR-0038 K1): çekici, kamyon, panelvan gibi motorlu araçlar ve dorse/römork gibi çekilen araçlar aynı kayıttır; ayrımı
/// araç türünün kategorisi yapar (<see cref="FleetModule.VehicleKindCategories"/>). Plaka tenant içinde benzersizdir ve boşluksuz büyük harfle
/// saklanır. Taşeron araçta taşıyıcı firma (<c>core.Party</c>, "Taşıyıcı" ilişki rolü) zorunludur. Araç silinmez, pasife alınır (K12).
/// Lojistik ve araç kiralama araca <c>IVehicleDirectory</c> ile başvurur; bu tabloya dokunmaz.
/// </summary>
public sealed class Vehicle : FleetEntity
{
    public const int PlateMaxLength = 20;
    public const int VinLength = 17;
    public const int BrandMaxLength = 100;
    public const int ModelMaxLength = 100;
    public const int NotesMaxLength = 2000;

    public static class Ownerships
    {
        public const string Owned = "owned";
        public const string Leased = "leased";
        public const string Subcontracted = "subcontracted";

        public static IReadOnlyList<string> All { get; } = [Owned, Leased, Subcontracted];
    }

    public static class Statuses
    {
        public const string Active = "active";
        public const string Maintenance = "maintenance";
        public const string Passive = "passive";

        public static IReadOnlyList<string> All { get; } = [Active, Maintenance, Passive];
    }

    /// <summary>Plaka: boşluksuz, büyük harf (ör. "34ABC123"); tenant içinde benzersiz.</summary>
    public string Plate { get; internal set; } = string.Empty;

    /// <summary>Araç türü (Y9b seçim listesi <c>fleet.vehicle-kind</c>); kod türün adına değil kategorisine bakar.</summary>
    public string KindKey { get; internal set; } = string.Empty;

    public string Ownership { get; internal set; } = Ownerships.Owned;

    /// <summary>Taşeron araçta aracın sahibi taşıyıcı firma; diğer mülkiyet türlerinde boştur.</summary>
    public Guid? CarrierPartyId { get; internal set; }

    public string? Brand { get; internal set; }

    public string? Model { get; internal set; }

    public int? ModelYear { get; internal set; }

    /// <summary>Şasi numarası (17 karakter, büyük harf); verilirse tenant içinde benzersiz.</summary>
    public string? Vin { get; internal set; }

    /// <summary>Taşıma kapasitesi (kg).</summary>
    public decimal? PayloadKg { get; internal set; }

    /// <summary>Yük hacmi (m³).</summary>
    public decimal? VolumeM3 { get; internal set; }

    /// <summary>Yükleme metresi (LDM).</summary>
    public decimal? LoadingMeters { get; internal set; }

    public int? PalletCapacity { get; internal set; }

    /// <summary>Etkin, bakımda ya da pasif. Pasif araç yeni sefere atanmaz; geçmiş kayıtları korunur.</summary>
    public string Status { get; internal set; } = Statuses.Active;

    public string? Notes { get; internal set; }
}
