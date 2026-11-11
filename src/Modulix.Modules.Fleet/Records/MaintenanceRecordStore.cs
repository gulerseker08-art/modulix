using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Fleet.Domain;

namespace Modulix.Modules.Fleet.Records;

/// <summary>
/// Bakım kayıtları (ADR-0038 dilim 1b). Araç yalnızca oluştururken seçilir. Bakım tarihi ileri bir gün olamaz; sonraki bakım tarihi bakım
/// tarihinden, sonraki bakım kilometresi bakım kilometresinden büyük olmalıdır; tutar verilirse para birimi zorunludur. Her yazma ve silmede
/// aracın türetilmiş bakım alanları (son bakım, kilometre, sonraki bakım tarihi ve kilometresi) en son bakım kaydından yeniden hesaplanır;
/// değişiklik bakım kaydıyla aynı işlemde kaydedilir (depodaki defter → bakiye kalıbı).
/// </summary>
internal sealed partial class MaintenanceRecordStore(FleetStoreContext context) : FleetRecordStore<MaintenanceRecord>(context)
{
    public override string EntityKey => FleetModule.Entities.Maintenance;

    protected override string ReadPermission => FleetModule.Permissions.MaintenanceRead;

    protected override string WritePermission => FleetModule.Permissions.MaintenanceWrite;

    protected override IReadOnlyList<RecordField<MaintenanceRecord>> Fields { get; } =
    [
        Field.Reference<MaintenanceRecord, Vehicle>("vehicleId", "Araç bulunamadı.", m => m.VehicleId, (m, v) => m.VehicleId = v!.Value, required: true, createOnly: true),
        Field.ListChoice<MaintenanceRecord>("kindKey", FleetModule.Lists.MaintenanceKind, m => m.KindKey, (m, v) => m.KindKey = v ?? string.Empty),
        Field.Date<MaintenanceRecord>("performedOn", m => m.PerformedOn, (m, v) => m.PerformedOn = v!.Value, required: true),
        Field.Integer<MaintenanceRecord>("odometerKm", m => m.OdometerKm, (m, v) => m.OdometerKm = v, 0, MaintenanceRecord.MaxKm),
        Field.PartyRef<MaintenanceRecord>("supplierPartyId", "Servis firması bulunamadı.", m => m.SupplierPartyId, (m, v) => m.SupplierPartyId = v),
        Field.Decimal<MaintenanceRecord>("cost", m => m.Cost, (m, v) => m.Cost = v, 2, 0m, 100_000_000m),
        Field.Text<MaintenanceRecord>("currency", 3, m => m.Currency, (m, v) => m.Currency = v, upperCase: true),
        Field.Date<MaintenanceRecord>("nextDueDate", m => m.NextDueDate, (m, v) => m.NextDueDate = v),
        Field.Integer<MaintenanceRecord>("nextDueKm", m => m.NextDueKm, (m, v) => m.NextDueKm = v, 0, MaintenanceRecord.MaxKm),
        Field.Text<MaintenanceRecord>("description", MaintenanceRecord.DescriptionMaxLength, m => m.Description, (m, v) => m.Description = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<MaintenanceRecord>> Filters { get; } = new Dictionary<string, RecordFilter<MaintenanceRecord>>
    {
        ["vehicleId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.VehicleId == id) : null,
        ["kindKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(m => m.KindKey == v) : null,
        ["supplierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.SupplierPartyId == id) : null,
        ["performedFrom"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(m => m.PerformedOn >= day) : null,
    };

    // Varsayılan: en yeni bakım üstte.
    protected override IOrderedQueryable<MaintenanceRecord> Order(IQueryable<MaintenanceRecord> source) =>
        source.OrderByDescending(m => m.PerformedOn).ThenByDescending(m => m.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("performedOn", "kindKey", "odometerKm", "cost", "nextDueDate", "createdAt");

    protected override IOrderedQueryable<MaintenanceRecord> Order(IQueryable<MaintenanceRecord> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "performedOn" => By(source, m => m.PerformedOn, desc).ThenByDescending(m => m.CreatedAt),
            "kindKey" => By(source, m => m.KindKey, desc).ThenByDescending(m => m.PerformedOn),
            "odometerKm" => ByNullsLast(source, m => m.OdometerKm, desc).ThenByDescending(m => m.PerformedOn),
            "cost" => ByNullsLast(source, m => m.Cost, desc).ThenByDescending(m => m.PerformedOn),
            "nextDueDate" => ByNullsLast(source, m => m.NextDueDate, desc).ThenByDescending(m => m.PerformedOn),
            "createdAt" => By(source, m => m.CreatedAt, desc),
            _ => Order(source),
        };
    }

    // Plakayla ve açıklamayla aranır (plaka boşluksuz büyük harfe çevrilerek).
    protected override IQueryable<MaintenanceRecord> Search(IQueryable<MaintenanceRecord> source, string term)
    {
        var plate = VehicleRecordStore.Normalize(term);
        var vehicles = Context.Data.Set<Vehicle>().AsNoTracking().Where(v => v.Plate.Contains(plate)).Select(v => v.Id);
        return source.Where(m => vehicles.Contains(m.VehicleId) || (m.Description != null && m.Description.Contains(term)));
    }

    protected override async Task AppliedAsync(MaintenanceRecord entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        // Tenant saat dilimi farkı için bir gün pay bırakılır; ileri tarihli plan "sonraki bakım" alanına yazılır.
        if (entity.PerformedOn > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            errors.Add(new RecordFieldError("performedOn", "Bakım tarihi ileri bir gün olamaz; planlanan bakımı \"Sonraki bakım tarihi\" alanına yazın."));
        }

        if (entity.NextDueDate is { } nextDate && nextDate <= entity.PerformedOn)
        {
            errors.Add(new RecordFieldError("nextDueDate", "Sonraki bakım tarihi bakım tarihinden sonra olmalı."));
        }

        if (entity.NextDueKm is { } nextKm && entity.OdometerKm is { } km && nextKm <= km)
        {
            errors.Add(new RecordFieldError("nextDueKm", "Sonraki bakım kilometresi bakım kilometresinden büyük olmalı."));
        }

        if (entity.Currency is { } currency && !CurrencyPattern().IsMatch(currency))
        {
            errors.Add(new RecordFieldError("currency", "Üç harfli para birimi girin (ör. TRY)."));
        }
        else if (entity.Cost is not null && entity.Currency is null)
        {
            errors.Add(new RecordFieldError("currency", "Tutar girildiyse para birimi zorunludur."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        await RefreshVehicleAsync(entity.VehicleId, entity, null, cancellationToken);
    }

    protected override Task BeforeDeleteAsync(MaintenanceRecord entity, CancellationToken cancellationToken) =>
        RefreshVehicleAsync(entity.VehicleId, null, entity.Id, cancellationToken);

    /// <summary>
    /// Aracın türetilmiş bakım alanlarını yeniden hesaplar: en son bakım (tarih, sonra kayıt anı) son bakım ve sonraki bakım değerlerini,
    /// kayıtların en yüksek kilometresi aracın kilometresini verir. <paramref name="current"/> henüz kaydedilmemiş (yeni ya da değişmiş)
    /// kayıttır; <paramref name="removed"/> silinmekte olan kayıttır.
    /// </summary>
    private async Task RefreshVehicleAsync(Guid vehicleId, MaintenanceRecord? current, Guid? removed, CancellationToken cancellationToken)
    {
        var vehicle = await Context.Data.Set<Vehicle>().SingleAsync(v => v.Id == vehicleId, cancellationToken);
        var skip = current?.Id ?? removed ?? Guid.Empty;
        var records = await Context.Data.Set<MaintenanceRecord>().AsNoTracking()
            .Where(m => m.VehicleId == vehicleId && m.Id != skip)
            .Select(m => new { m.PerformedOn, m.CreatedAt, m.OdometerKm, m.NextDueDate, m.NextDueKm })
            .ToListAsync(cancellationToken);
        if (current is not null)
        {
            records.Add(new { current.PerformedOn, current.CreatedAt, current.OdometerKm, current.NextDueDate, current.NextDueKm });
        }

        var latest = records.OrderByDescending(r => r.PerformedOn).ThenByDescending(r => r.CreatedAt).FirstOrDefault();
        vehicle.ApplyMaintenance(latest?.PerformedOn, records.Max(r => r.OdometerKm), latest?.NextDueDate, latest?.NextDueKm);
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
