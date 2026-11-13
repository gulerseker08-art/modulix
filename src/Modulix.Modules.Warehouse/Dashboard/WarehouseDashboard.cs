
namespace Modulix.Modules.Warehouse.Dashboard;

/// <summary>Tesis sayaçları: etkin tesis ve bunların gümrük uzantılı olanları.</summary>
public sealed record FacilityCounts(int Active, int CustomsEnabled);

/// <summary>Bir tesisin lokasyon doluluğu (stok tutulabilen etkin uç lokasyonlar; en az bir partinin sıfırdan büyük bakiyesi olan dolu sayılır).</summary>
public sealed record FacilityOccupancy(Guid FacilityId, string Code, string Name, bool CustomsEnabled, int Locations, int Occupied);

/// <summary>
/// Lokasyon doluluğu. Kapasite (m³, palet) tanımı olmadığı için doluluk lokasyon sayısıyla ölçülür: stok tutulabilen yer, alt düğümü olmayan etkin lokasyondur.
/// <see cref="PassiveWithStock"/> pasife alınmış ama hâlâ bakiyesi olan lokasyonlardır (boşaltılması gerekir).
/// </summary>
public sealed record OccupancyView(int Locations, int Occupied, int PassiveWithStock, IReadOnlyList<FacilityOccupancy> ByFacility);

/// <summary>Stok kartı sayaçları: etkin kart ve dönem içinde eklenen kart.</summary>
public sealed record StockItemCounts(int Active, int NewInPeriod);

/// <summary>Beyanname durumu başına sayı; ad ve kategori firmanın listesinden gelir (K18).</summary>
public sealed record DeclarationStatusCount(string Key, string Label, string? Category, int Count);

/// <summary>Taslakta bekleyen beyanname (en eskiler).</summary>
public sealed record StaleDeclaration(Guid Id, string RegistrationNo, DateTimeOffset CreatedAt);

/// <summary>
/// Beyanname özeti: durum dağılımı (iptal dahil, bütün kayıtlar) ve <see cref="WarehouseDashboardRules.StaleDraftDays"/> günden uzun süredir taslak kategorisinde kalanlar.
/// </summary>
public sealed record DeclarationView(IReadOnlyList<DeclarationStatusCount> ByStatus, int StaleDrafts, IReadOnlyList<StaleDeclaration> StaleSample);

/// <summary>Bir gündeki hareket sayıları, türün kategorisine göre (UTC gün sınırları; liste süzgeçleriyle aynı).</summary>
public sealed record DailyMovement(DateOnly Day, int Inbound, int Outbound, int Transfer, int Adjustment);

/// <summary>
/// Son hareket. Kalem, parti ve lokasyon bilgisi ayrı izinlerdir: kullanıcının o kaydı okuma izni yoksa ilgili alan boş gelir.
/// </summary>
public sealed record RecentMovement(
    Guid Id, string TypeKey, string TypeLabel, string? Category, decimal Quantity, DateTimeOffset OccurredAt,
    string? StockItemCode, string? StockItemName, string? LotNo, string? FromLocation, string? ToLocation);

/// <summary>Hareket özeti: dönemin günlük sayıları, dönem toplamları ve son hareketler.</summary>
public sealed record MovementView(IReadOnlyList<DailyMovement> Daily, int Inbound, int Outbound, int Transfer, IReadOnlyList<RecentMovement> Recent);

/// <summary>Bitişi yaklaşan vekâletler: etkin ve bitişi bugünle <see cref="WarehouseDashboardRules.ExpiryWindowDays"/> gün sonrası arasında olanlar.</summary>
public sealed record AttorneyAlert(int Expiring, DateOnly? Nearest);

/// <summary>
/// Depo ve antrepo panosu. Her bölüm kendi okuma iznine bağlıdır: izni olmayan kullanıcıya bölüm <c>null</c> gelir (yalnızca arayüzde gizlenmez).
/// Tesis süzgeci (<see cref="FacilityId"/>) lokasyon, doluluk ve hareket bölümlerine uygulanır; beyanname, vekâlet ve stok kartı tesise bağlı değildir.
/// </summary>
public sealed record WarehouseDashboardView(
    DateOnly Today,
    int Days,
    Guid? FacilityId,
    FacilityCounts? Facilities,
    OccupancyView? Occupancy,
    StockItemCounts? StockItems,
    DeclarationView? Declarations,
    MovementView? Movements,
    AttorneyAlert? Attorneys);

/// <summary>Panonun eşikleri ve kabul ettiği dönemler.</summary>
public static class WarehouseDashboardRules
{
    /// <summary>Taslakta bu kadar günden uzun kalan beyanname "dikkat" listesine girer.</summary>
    public const int StaleDraftDays = 7;

    /// <summary>Vekâlet bitişi bu kadar gün içindeyse uyarı verilir.</summary>
    public const int ExpiryWindowDays = 30;

    public const int RecentLimit = 6;

    public const int StaleSampleLimit = 3;

    /// <summary>Panonun kabul ettiği dönem uzunlukları (gün).</summary>
    public static IReadOnlyList<int> AllowedDays { get; } = [7, 14, 30];
}

public interface IWarehouseDashboard
{
    /// <summary>Panoyu hesaplar. Tesis bu tenant'ta yoksa <c>null</c> döner (başka tenant'ın tesisi de bulunamaz).</summary>
    Task<WarehouseDashboardView?> GetAsync(Guid? facilityId, int days, CancellationToken cancellationToken);
}

