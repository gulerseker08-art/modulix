using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Fleet;
using Modulix.Modules.Fleet.Domain;

namespace Modulix.Modules.Fleet.Records;

/// <summary>
/// <see cref="IVehicleDirectory"/> uygulaması: araç özetini tenant filtresi ve RLS altında okur. Filo modülü kapalıysa boş döner. Kayıt
/// izni sormaz: çağıran modül kendi kaydının (sefer, kiralama) iznini zaten denetler; özet hassas alan taşımaz.
/// </summary>
internal sealed class VehicleDirectory(FleetStoreContext context, ICurrentAccess access) : IVehicleDirectory
{
    public const int MaxIds = 500;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => access.IsModuleEnabledAsync(FleetModule.ModuleKey, cancellationToken);

    public async Task<IReadOnlyList<VehicleSummary>> GetAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        if (vehicleIds.Count == 0 || !await IsAvailableAsync(cancellationToken))
        {
            return [];
        }

        if (vehicleIds.Count > MaxIds)
        {
            throw new ArgumentException($"En çok {MaxIds} araç birlikte okunabilir.", nameof(vehicleIds));
        }

        var ids = vehicleIds.Distinct().ToList();
        var vehicles = await context.Data.Set<Vehicle>().AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken);
        var kinds = await context.ChoiceLists.GetAsync(FleetModule.Lists.VehicleKind, cancellationToken);
        return
        [
            .. vehicles.Select(v => new VehicleSummary(
                v.Id, v.Plate, v.KindKey, kinds.Find(v.KindKey)?.Category, v.Ownership, v.CarrierPartyId, v.Status,
                v.PayloadKg, v.VolumeM3, v.LoadingMeters, v.PalletCapacity)),
        ];
    }
}
