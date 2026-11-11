using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Sürücü kartları (ADR-0038 K2, dilim 3). Kişi yalnızca oluştururken seçilir ve bir kişinin tek sürücü kartı olur. Taşeron sürücüde
/// "Taşıyıcı" rollü firma zorunludur, kadrolu sürücüde taşıyıcı seçilmez. Varsayılan araç Filo'dan okunur (<see cref="Core.Contracts.Fleet.IVehicleDirectory"/>):
/// araç bu tenant'ta olmalı, motorlu (dorse/römork değil) ve pasif olmamalıdır; Filo kapalıysa araç seçilemez. Sürücü silinmez (K12).
/// Liste kişinin adına göre sıralanır ve kişinin adı, soyadı, telefonu ve e-postasıyla aranır.
/// </summary>
internal sealed class DriverRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<Driver>(context)
{
    public override string EntityKey => LogisticsModule.Entities.Driver;

    protected override string ReadPermission => LogisticsModule.Permissions.DriversRead;

    protected override string WritePermission => LogisticsModule.Permissions.DriversWrite;

    protected override IReadOnlyList<RecordField<Driver>> Fields { get; } =
    [
        Field.PersonRef<Driver>("personId", "Kişi bulunamadı.", d => d.PersonId, (d, v) => d.PersonId = v!.Value, required: true, createOnly: true),
        Field.Choice<Driver>("driverType", Driver.Types.All, d => d.DriverType, (d, v) => d.DriverType = v),
        Field.PartyRef<Driver>("carrierPartyId", "Taşıyıcı firma bulunamadı.", d => d.CarrierPartyId, (d, v) => d.CarrierPartyId = v),
        Field.Id<Driver>("defaultVehicleId", d => d.DefaultVehicleId, (d, v) => d.DefaultVehicleId = v),
        Field.Choice<Driver>("status", Driver.Statuses.All, d => d.Status, (d, v) => d.Status = v),
        Field.Text<Driver>("notes", Driver.NotesMaxLength, d => d.Notes, (d, v) => d.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Driver>> Filters { get; } = new Dictionary<string, RecordFilter<Driver>>
    {
        ["status"] = (q, v) => Driver.Statuses.All.Contains(v) ? q.Where(d => d.Status == v) : null,
        ["driverType"] = (q, v) => Driver.Types.All.Contains(v) ? q.Where(d => d.DriverType == v) : null,
        ["carrierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(d => d.CarrierPartyId == id) : null,
        ["personId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(d => d.PersonId == id) : null,
        ["defaultVehicleId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(d => d.DefaultVehicleId == id) : null,
    };

    private IQueryable<Person> People => Context.Data.Set<Person>().AsNoTracking();

    protected override IOrderedQueryable<Driver> Order(IQueryable<Driver> source) =>
        source.OrderBy(d => People.Where(p => p.Id == d.PersonId).Select(p => p.LastName).FirstOrDefault())
            .ThenBy(d => People.Where(p => p.Id == d.PersonId).Select(p => p.FirstName).FirstOrDefault());

    // Başlığa göre sıralama: ad (kişinin soyadı, adı), tür, durum, kayıt zamanı; eşitler ada göre.
    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("name", "driverType", "status", "createdAt");

    protected override IOrderedQueryable<Driver> Order(IQueryable<Driver> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "name" => By(source, d => People.Where(p => p.Id == d.PersonId).Select(p => p.LastName + " " + p.FirstName).FirstOrDefault(), desc),
            "driverType" => By(source, d => d.DriverType, desc).ThenBy(d => d.CreatedAt),
            "status" => By(source, d => d.Status, desc).ThenBy(d => d.CreatedAt),
            "createdAt" => By(source, d => d.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<Driver> Search(IQueryable<Driver> source, string term)
    {
        var matches = People.Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)
            || (p.Phone != null && p.Phone.Contains(term)) || (p.Email != null && p.Email.Contains(term))).Select(p => p.Id);
        return source.Where(d => matches.Contains(d.PersonId));
    }

    protected override async Task AppliedAsync(Driver entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (isNew && await Context.Data.Set<Driver>().AsNoTracking().AnyAsync(d => d.PersonId == entity.PersonId && d.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Bu kişinin zaten bir sürücü kartı var."));
        }

        if (entity.DriverType == Driver.Types.Subcontracted)
        {
            if (entity.CarrierPartyId is not { } carrier)
            {
                errors.Add(new RecordFieldError("carrierPartyId", "Taşeron sürücüde taşıyıcı firma seçilmelidir."));
            }
            else if (isNew || values.ContainsKey("carrierPartyId") || values.ContainsKey("driverType"))
            {
                // Rol, taşıyıcı ya da tür değişince denetlenir; firmadan rol sonradan kalksa da kayıtlı kart bozulmaz.
                var roles = await Context.Data.Set<Party>().AsNoTracking()
                    .Where(p => p.Id == carrier).Select(p => p.RelationshipRoles).SingleOrDefaultAsync(cancellationToken);
                if (!(roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(LogisticsModule.RelationshipRoles.Carrier))
                {
                    errors.Add(new RecordFieldError(
                        "carrierPartyId", "Seçilen firma \"Taşıyıcı\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin."));
                }
            }
        }
        else if (entity.CarrierPartyId is not null)
        {
            errors.Add(new RecordFieldError("carrierPartyId", "Taşıyıcı firma yalnızca taşeron sürücüde seçilir."));
        }

        if (entity.DefaultVehicleId is { } vehicleId && (isNew || values.ContainsKey("defaultVehicleId")))
        {
            if (!await Context.Vehicles.IsAvailableAsync(cancellationToken))
            {
                errors.Add(new RecordFieldError("defaultVehicleId", "Filo modülü bu firmada açık değil; araç seçilemez."));
            }
            else if ((await Context.Vehicles.GetAsync([vehicleId], cancellationToken)).SingleOrDefault() is not { } vehicle)
            {
                errors.Add(new RecordFieldError("defaultVehicleId", "Araç bulunamadı."));
            }
            else if (vehicle.KindCategory == LogisticsModule.VehicleKindTowed)
            {
                errors.Add(new RecordFieldError("defaultVehicleId", "Dorse ve römork sürücüye varsayılan araç olarak atanamaz; çekici ya da kamyon seçin."));
            }
            else if (vehicle.Status == "passive")
            {
                errors.Add(new RecordFieldError("defaultVehicleId", "Pasif araç sürücüye atanamaz."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(Driver entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Sürücü kartları silinmez; çalışmayan sürücünün durumunu \"Pasif\" yapın.");
}
