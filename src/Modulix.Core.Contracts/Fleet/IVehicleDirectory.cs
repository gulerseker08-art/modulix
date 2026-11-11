namespace Modulix.Core.Contracts.Fleet;

/// <summary>
/// Araç kimliğinin okuma sözleşmesi (ADR-0038 K1). Araç Filo modülündedir (<c>fleet.Vehicles</c>); lojistik ve araç kiralama araca
/// bu arayüzle başvurur, araç tablosuna dokunmaz. Sonuçlar aktif tenant'la sınırlıdır (sorgu filtresi + RLS). Filo modülü bu tenant'ta
/// kapalıysa <see cref="IsAvailableAsync"/> false, okuma boş döner: çağıran modül araç seçimini sunmaz ama çalışmaya devam eder
/// (yumuşak bağımlılık, ADR-0038 bölüm 9).
/// </summary>
public interface IVehicleDirectory
{
    /// <summary>Filo modülü aktif tenant için açık mı?</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Verilen kimliklerdeki araçlar (bulunmayanlar sonuçta yoktur). En çok 500 kimlik.</summary>
    Task<IReadOnlyList<VehicleSummary>> GetAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);
}

/// <summary>
/// Aracın modüller arası özeti. <see cref="KindCategory"/> araç türünün kategorisidir (<c>powered</c>: motorlu, <c>towed</c>: dorse/römork);
/// çağıran kod türün adına değil kategoriye bakar. Kapasite alanları boş olabilir.
/// </summary>
public sealed record VehicleSummary(
    Guid Id,
    string Plate,
    string KindKey,
    string? KindCategory,
    string Ownership,
    Guid? CarrierPartyId,
    string Status,
    decimal? PayloadKg,
    decimal? VolumeM3,
    decimal? LoadingMeters,
    int? PalletCapacity)
{
    /// <summary>Etkin araç (bakımda ya da pasif değil); yeni atamaya uygun.</summary>
    public bool IsActive => Status == "active";
}
