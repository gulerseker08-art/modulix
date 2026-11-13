using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics.Dashboard;

/// <summary>
/// Lojistik operasyon panosu (ADR-0038 dilim 9a). Her bölüm yalnızca o kaydı okuma izni olan kullanıcıya hesaplanır; izni olmayan bölüm
/// <c>null</c> döner. Gün firmanın saat dilimindedir (<see cref="ITenantClock"/>). Panoda tutar yoktur (navlun hassas alandır; kârlılık dilim 9b).
/// </summary>
public sealed record LogisticsDashboard(
    DateOnly Today,
    DashboardRequests? Requests,
    DashboardShipments? Shipments,
    DashboardTrips? Trips,
    DashboardDeliveries? Deliveries,
    DashboardCredentials? Credentials)
{
    public bool IsEmpty => Requests is null && Shipments is null && Trips is null && Deliveries is null && Credentials is null;
}

/// <summary>Onay bekleyen talepler ve en eskisinin bekleme süresi (gün).</summary>
public sealed record DashboardRequests(int Awaiting, int? OldestWaitingDays);

/// <summary>
/// Açık sevkiyatlar: planlama bekleyen, yüklemesi yarına kadar olup hâlâ planlanmamış, en geç teslim günü geçmiş (geciken), yolda;
/// açık durumlara göre dağılım ve en çok geciken ilk kayıtlar.
/// </summary>
public sealed record DashboardShipments(
    int Pending, int PickupSoonUnplanned, int Late, int InTransit, IReadOnlyList<DashboardStatusCount> ByStatus, IReadOnlyList<DashboardLateShipment> LateItems,
    IReadOnlyList<DashboardRoute> Routes);

/// <summary>Güzergâh özeti (harita): etkin (planlı, yüklenen, yoldaki) sevkiyatların yükleme ve teslim şehri (kayıttaki metin) ve sayısı.</summary>
public sealed record DashboardRoute(string From, string To, int Count);

public sealed record DashboardStatusCount(string Status, int Count);

public sealed record DashboardLateShipment(Guid Id, string PickupCity, string DeliveryCity, DateOnly DeliveryBy, string Status, int DaysLate);

/// <summary>Sefer sayıları ve öne çıkan seferler (yoldakiler, hazırlar ve bugün-yarın başlayacak planlananlar) durak ilerlemesiyle.</summary>
public sealed record DashboardTrips(int Planning, int Ready, int InProgress, int StartingToday, IReadOnlyList<DashboardTrip> Active);

public sealed record DashboardTrip(Guid Id, string Number, string Status, DateOnly PlannedStart, Guid? VehicleId, Guid? TrailerId, int Stops, int Arrived, string? NextCity);

/// <summary>Son 30 günün teslim sonuçları (başarı oranı = teslim / bütün sonuçlar) ve son 14 günün günlük dağılımı (eskiden yeniye).</summary>
public sealed record DashboardDeliveries(int Delivered, int Partial, int Failed, int Damaged, decimal? SuccessPercent, IReadOnlyList<DashboardDay> Days);

public sealed record DashboardDay(DateOnly Day, int Delivered, int Partial, int Failed);

/// <summary>Etkin sürücülerin süresi dolmuş ve 30 gün içinde dolacak belgeleri (her türün en uzun geçerli belgesine bakılır).</summary>
public sealed record DashboardCredentials(int Expired, int ExpiringSoon, IReadOnlyList<DashboardCredential> Items);

public sealed record DashboardCredential(Guid DriverId, string DriverName, string TypeLabel, DateOnly ValidTo);

public interface ILogisticsDashboard
{
    Task<LogisticsDashboard> GetAsync(CancellationToken cancellationToken);
}

internal sealed class LogisticsDashboardService(
    LogisticsStoreContext context, ICurrentAccess access, ITenantClock clock, ICredentialDirectory? credentials = null) : ILogisticsDashboard
{
    public const int ListLimit = 6;
    public const int TripLimit = 8;
    public const int RouteLimit = 8;
    public const int DeliveryWindowDays = 30;
    public const int ChartDays = 14;
    public const int CredentialWindowDays = 30;

    private static readonly string[] OpenShipments = [Shipment.Statuses.Pending, Shipment.Statuses.Planned, Shipment.Statuses.Loading, Shipment.Statuses.InTransit];

    public async Task<LogisticsDashboard> GetAsync(CancellationToken cancellationToken)
    {
        var day = await clock.GetAsync(cancellationToken);
        var today = day.Today;
        var granted = await access.GetPermissionsAsync(cancellationToken);
        bool Can(string permission) => granted.Contains(permission);

        return new LogisticsDashboard(
            today,
            Can(LogisticsModule.Permissions.RequestsRead) ? await RequestsAsync(day, cancellationToken) : null,
            Can(LogisticsModule.Permissions.ShipmentsRead) ? await ShipmentsAsync(today, cancellationToken) : null,
            Can(LogisticsModule.Permissions.TripsRead) ? await TripsAsync(today, cancellationToken) : null,
            Can(LogisticsModule.Permissions.ShipmentsRead) ? await DeliveriesAsync(day, cancellationToken) : null,
            Can(LogisticsModule.Permissions.DriversRead) ? await CredentialsAsync(today, cancellationToken) : null);
    }

    private async Task<DashboardRequests> RequestsAsync(TenantDay day, CancellationToken cancellationToken)
    {
        var waiting = context.Data.Set<TransportRequest>().AsNoTracking().Where(r => r.Status == TransportRequest.Statuses.New);
        var count = await waiting.CountAsync(cancellationToken);
        var oldest = await waiting.MinAsync(r => (DateTimeOffset?)r.CreatedAt, cancellationToken);
        return new DashboardRequests(count, oldest is { } created ? Math.Max(0, (int)(day.UtcNow - created).TotalDays) : null);
    }

    private async Task<DashboardShipments> ShipmentsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var open = context.Data.Set<Shipment>().AsNoTracking().Where(s => OpenShipments.Contains(s.Status));
        var byStatus = await open.GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var soon = today.AddDays(1);
        var pickupSoon = await open.CountAsync(s => s.Status == Shipment.Statuses.Pending && s.PickupDate <= soon, cancellationToken);
        var late = open.Where(s => s.DeliveryBy != null && s.DeliveryBy < today);
        var lateCount = await late.CountAsync(cancellationToken);
        var lateItems = await late.OrderBy(s => s.DeliveryBy).ThenBy(s => s.Id).Take(ListLimit)
            .Select(s => new { s.Id, s.PickupCity, s.DeliveryCity, DeliveryBy = s.DeliveryBy!.Value, s.Status })
            .ToListAsync(cancellationToken);

        // Harita: etkin sevkiyatların güzergâhları (şehir metni kayıttaki gibi); en yoğun ilk RouteLimit güzergâh.
        var routes = await open.Where(s => s.Status != Shipment.Statuses.Pending)
            .GroupBy(s => new { s.PickupCity, s.DeliveryCity })
            .Select(g => new { g.Key.PickupCity, g.Key.DeliveryCity, Count = g.Count() })
            .OrderByDescending(r => r.Count).ThenBy(r => r.PickupCity).ThenBy(r => r.DeliveryCity)
            .Take(RouteLimit).ToListAsync(cancellationToken);

        int Of(string status) => byStatus.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        return new DashboardShipments(
            Of(Shipment.Statuses.Pending), pickupSoon, lateCount, Of(Shipment.Statuses.InTransit),
            [.. OpenShipments.Select(s => new DashboardStatusCount(s, Of(s)))],
            [.. lateItems.Select(s => new DashboardLateShipment(s.Id, s.PickupCity, s.DeliveryCity, s.DeliveryBy, s.Status, today.DayNumber - s.DeliveryBy.DayNumber))],
            [.. routes.Select(r => new DashboardRoute(r.PickupCity, r.DeliveryCity, r.Count))]);
    }

    private async Task<DashboardTrips> TripsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var trips = context.Data.Set<Trip>().AsNoTracking();
        var counts = await trips.Where(t => t.Status == Trip.Statuses.Planning || t.Status == Trip.Statuses.Ready || t.Status == Trip.Statuses.InProgress)
            .GroupBy(t => t.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var startingToday = await trips.CountAsync(
            t => t.PlannedStart == today && (t.Status == Trip.Statuses.Planning || t.Status == Trip.Statuses.Ready), cancellationToken);

        // Öne çıkanlar: önce yoldakiler, sonra hazırlar, sonra bugün-yarın başlayacak planlananlar; her grupta en erken başlangıç önce.
        var soon = today.AddDays(1);
        var active = await trips
            .Where(t => t.Status == Trip.Statuses.InProgress || t.Status == Trip.Statuses.Ready || (t.Status == Trip.Statuses.Planning && t.PlannedStart <= soon))
            .OrderBy(t => t.Status == Trip.Statuses.InProgress ? 0 : t.Status == Trip.Statuses.Ready ? 1 : 2).ThenBy(t => t.PlannedStart).ThenBy(t => t.Number)
            .Take(TripLimit)
            .Select(t => new { t.Id, t.Number, t.Status, t.PlannedStart, t.VehicleId, t.TrailerId })
            .ToListAsync(cancellationToken);
        List<Guid> ids = [.. active.Select(t => t.Id)];
        var stops = await context.Data.Set<TripStop>().AsNoTracking().Where(s => ids.Contains(s.TripId))
            .Select(s => new { s.TripId, s.Sequence, s.City, s.ArrivedAt }).ToListAsync(cancellationToken);

        int Of(string status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        return new DashboardTrips(
            Of(Trip.Statuses.Planning), Of(Trip.Statuses.Ready), Of(Trip.Statuses.InProgress), startingToday,
            [
                .. active.Select(t =>
                {
                    var own = stops.Where(s => s.TripId == t.Id).OrderBy(s => s.Sequence).ToList();
                    return new DashboardTrip(
                        t.Id, t.Number, t.Status, t.PlannedStart, t.VehicleId, t.TrailerId, own.Count, own.Count(s => s.ArrivedAt is not null),
                        own.FirstOrDefault(s => s.ArrivedAt is null)?.City);
                }),
            ]);
    }

    private async Task<DashboardDeliveries> DeliveriesAsync(TenantDay day, CancellationToken cancellationToken)
    {
        var from = day.StartOfDay(day.Today.AddDays(-(DeliveryWindowDays - 1)));
        var proofs = await context.Data.Set<DeliveryProof>().AsNoTracking().Where(p => p.ReceivedAt >= from)
            .Select(p => new { p.Outcome, p.ReceivedAt, p.Damaged }).ToListAsync(cancellationToken);
        var delivered = proofs.Count(p => p.Outcome == DeliveryProof.Outcomes.Delivered);
        var partial = proofs.Count(p => p.Outcome == DeliveryProof.Outcomes.Partial);
        var failed = proofs.Count(p => p.Outcome == DeliveryProof.Outcomes.Failed);
        decimal? success = proofs.Count == 0 ? null : Math.Round(delivered * 100m / proofs.Count, 1);

        DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, day.Zone).DateTime);
        var byDay = proofs.GroupBy(p => LocalDay(p.ReceivedAt)).ToDictionary(g => g.Key, g => g.ToList());
        List<DashboardDay> days = [];
        for (var d = day.Today.AddDays(-(ChartDays - 1)); d <= day.Today; d = d.AddDays(1))
        {
            var items = byDay.GetValueOrDefault(d) ?? [];
            days.Add(new DashboardDay(d, items.Count(p => p.Outcome == DeliveryProof.Outcomes.Delivered),
                items.Count(p => p.Outcome == DeliveryProof.Outcomes.Partial), items.Count(p => p.Outcome == DeliveryProof.Outcomes.Failed)));
        }

        return new DashboardDeliveries(delivered, partial, failed, proofs.Count(p => p.Damaged), success, days);
    }

    private async Task<DashboardCredentials> CredentialsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        if (credentials is null)
        {
            return new DashboardCredentials(0, 0, []);
        }

        var drivers = await context.Data.Set<Driver>().AsNoTracking().Where(d => d.Status != Driver.Statuses.Passive)
            .Join(context.Data.Set<Person>().AsNoTracking(), d => d.PersonId, p => p.Id, (d, p) => new { d.Id, Name = p.FirstName + " " + p.LastName })
            .OrderBy(d => d.Id).Take(500).ToListAsync(cancellationToken);
        if (drivers.Count == 0)
        {
            return new DashboardCredentials(0, 0, []);
        }

        var found = await credentials.ForOwnersAsync(LogisticsModule.Entities.Driver, [.. drivers.Select(d => d.Id)], cancellationToken);
        var limit = today.AddDays(CredentialWindowDays);

        // Her sürücünün her belge türünde en uzun geçerli (iptal edilmemiş) belgesi; süresiz belge sorun değildir.
        var latest = found.Where(c => c.Status == CredentialStatuses.Active)
            .GroupBy(c => (c.OwnerId, c.TypeKey))
            .Select(g => g.OrderBy(c => c.ValidTo is null ? 0 : 1).ThenByDescending(c => c.ValidTo).First())
            .Where(c => c.ValidTo is { } to && to <= limit)
            .OrderBy(c => c.ValidTo)
            .ToList();
        var names = drivers.ToDictionary(d => d.Id, d => d.Name);
        return new DashboardCredentials(
            latest.Count(c => c.ValidTo < today), latest.Count(c => c.ValidTo >= today),
            [.. latest.Take(ListLimit).Select(c => new DashboardCredential(c.OwnerId, names.GetValueOrDefault(c.OwnerId) ?? "—", c.TypeLabel, c.ValidTo!.Value))]);
    }
}
