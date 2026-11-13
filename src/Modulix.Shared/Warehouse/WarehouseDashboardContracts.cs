namespace Modulix.Shared.Warehouse;

/// <summary>
/// Depo ve antrepo panosu (<c>GET /api/warehouse/dashboard?facilityId=&amp;days=</c>). Her bölüm kendi okuma iznine bağlıdır; izni olmayan kullanıcıya bölüm
/// <c>null</c> gelir. Tesis süzgeci lokasyon, doluluk ve hareket bölümlerine uygulanır. Sayılar adettir; farklı birimlerdeki miktarlar toplanmaz.
/// </summary>
public sealed record WarehouseDashboardDto(
    DateOnly Today,
    int Days,
    Guid? FacilityId,
    WarehouseFacilityCountsDto? Facilities,
    WarehouseOccupancyDto? Occupancy,
    WarehouseStockItemCountsDto? StockItems,
    WarehouseDeclarationSummaryDto? Declarations,
    WarehouseMovementSummaryDto? Movements,
    WarehouseAttorneyAlertDto? Attorneys);

public sealed record WarehouseFacilityCountsDto(int Active, int CustomsEnabled);

public sealed record WarehouseFacilityOccupancyDto(Guid FacilityId, string Code, string Name, bool CustomsEnabled, int Locations, int Occupied);

/// <summary>Doluluk lokasyon sayısıyla ölçülür: stok tutulabilen yer alt düğümü olmayan etkin lokasyondur; bakiyesi olan dolu sayılır.</summary>
public sealed record WarehouseOccupancyDto(int Locations, int Occupied, int PassiveWithStock, IReadOnlyList<WarehouseFacilityOccupancyDto> ByFacility);

public sealed record WarehouseStockItemCountsDto(int Active, int NewInPeriod);

public sealed record WarehouseDeclarationStatusCountDto(string Key, string Label, string? Category, int Count);

public sealed record WarehouseStaleDeclarationDto(Guid Id, string RegistrationNo, DateTimeOffset CreatedAt);

public sealed record WarehouseDeclarationSummaryDto(
    IReadOnlyList<WarehouseDeclarationStatusCountDto> ByStatus, int StaleDrafts, int StaleDraftDays, IReadOnlyList<WarehouseStaleDeclarationDto> StaleSample);

public sealed record WarehouseDailyMovementDto(DateOnly Day, int Inbound, int Outbound, int Transfer, int Adjustment);

public sealed record WarehouseRecentMovementDto(
    Guid Id, string TypeKey, string TypeLabel, string? Category, decimal Quantity, DateTimeOffset OccurredAt,
    string? StockItemCode, string? StockItemName, string? LotNo, string? FromLocation, string? ToLocation);

public sealed record WarehouseMovementSummaryDto(
    IReadOnlyList<WarehouseDailyMovementDto> Daily, int Inbound, int Outbound, int Transfer, IReadOnlyList<WarehouseRecentMovementDto> Recent);

public sealed record WarehouseAttorneyAlertDto(int Expiring, int WindowDays, DateOnly? Nearest);
