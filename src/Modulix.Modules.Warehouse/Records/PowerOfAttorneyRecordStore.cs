using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Vekâlet kayıtları (K4). İthalatçı ve müşavir Core firmasıdır (K2) ve oluşturulduktan sonra değişmez (başka taraf yeni vekâlettir).
/// Kurallar: ithalatçı "ithalatçı / malik", müşavir "gümrük müşaviri" ilişki rolüne sahip olmalı (rol firma kaydından verilir),
/// ikisi aynı firma olamaz, bitiş tarihi başlangıçtan önce olamaz. Vekâlet iptali kaydı silmez, etkin bayrağını kapatır.
/// </summary>
internal sealed class PowerOfAttorneyRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<PowerOfAttorney>(context)
{
    public override string EntityKey => WarehouseModule.Entities.PowerOfAttorney;

    protected override string ReadPermission => WarehouseModule.Permissions.AttorneysRead;

    protected override string WritePermission => WarehouseModule.Permissions.AttorneysWrite;

    protected override IReadOnlyList<RecordField<PowerOfAttorney>> Fields { get; } =
    [
        Field.PartyRef<PowerOfAttorney>("importerPartyId", "İthalatçı bulunamadı.", p => p.ImporterPartyId, (p, v) => p.ImporterPartyId = v!.Value, required: true, createOnly: true),
        Field.PartyRef<PowerOfAttorney>("brokerPartyId", "Gümrük müşaviri bulunamadı.", p => p.BrokerPartyId, (p, v) => p.BrokerPartyId = v!.Value, required: true, createOnly: true),
        Field.Date<PowerOfAttorney>("validFrom", p => p.ValidFrom, (p, v) => p.ValidFrom = v!.Value, required: true),
        Field.Date<PowerOfAttorney>("validTo", p => p.ValidTo, (p, v) => p.ValidTo = v),
        Field.Text<PowerOfAttorney>("scope", PowerOfAttorney.ScopeMaxLength, p => p.Scope, (p, v) => p.Scope = v),
        Field.Text<PowerOfAttorney>("documentReference", PowerOfAttorney.DocumentMaxLength, p => p.DocumentReference, (p, v) => p.DocumentReference = v),
        Field.Flag<PowerOfAttorney>("isActive", p => p.IsActive, (p, v) => p.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<PowerOfAttorney>> Filters { get; } = new Dictionary<string, RecordFilter<PowerOfAttorney>>
    {
        // Firma silme/birleştirme koruması bu iki süzgeçle kullanım sayar (SharedRecordReferences).
        ["importerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(p => p.ImporterPartyId == id) : null,
        ["brokerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(p => p.BrokerPartyId == id) : null,
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(p => p.IsActive == flag) : null,
        // "validOn" (yyyy-AA-gg): o günde geçerli vekâletler (etkin ve tarih aralığında).
        ["validOn"] = (q, v) => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? q.Where(p => p.IsActive && p.ValidFrom <= day && (p.ValidTo == null || p.ValidTo >= day))
            : null,
    };

    protected override IOrderedQueryable<PowerOfAttorney> Order(IQueryable<PowerOfAttorney> source) =>
        source.OrderByDescending(p => p.ValidFrom).ThenBy(p => p.ImporterPartyId);

    // Liste başlıklarına göre sıralama. Taraflar firma adına göre sıralanmaz: firma görünürlüğü (CRM kapsamı) sıralamayla dolanılmasın.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("validFrom", "validTo", "isActive", "createdAt");

    protected override IOrderedQueryable<PowerOfAttorney> Order(IQueryable<PowerOfAttorney> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "validFrom" => By(source, p => p.ValidFrom, desc).ThenBy(p => p.ImporterPartyId),
            "validTo" => ByNullsLast(source, p => p.ValidTo, desc).ThenByDescending(p => p.ValidFrom),
            "isActive" => By(source, p => p.IsActive, desc).ThenByDescending(p => p.ValidFrom),
            "createdAt" => By(source, p => p.CreatedAt, desc).ThenByDescending(p => p.ValidFrom),
            _ => Order(source),
        };
    }

    // Firma adıyla arama yok: firma görünürlüğü (CRM kapsamı) bu ekrandan dolanılmasın; firma süzgeçleri seçim kutusuyla yapılır.
    protected override IQueryable<PowerOfAttorney> Search(IQueryable<PowerOfAttorney> source, string term) =>
        source.Where(p => (p.Scope != null && p.Scope.Contains(term)) || (p.DocumentReference != null && p.DocumentReference.Contains(term)));

    protected override async Task AppliedAsync(PowerOfAttorney entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (entity.ImporterPartyId == entity.BrokerPartyId)
        {
            errors.Add(new RecordFieldError("brokerPartyId", "İthalatçı ve gümrük müşaviri aynı firma olamaz."));
        }

        if (entity.ValidTo is { } to && to < entity.ValidFrom)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş tarihi başlangıç tarihinden önce olamaz."));
        }

        // Taraflar yalnızca oluştururken seçilir ve rolleri o anda denetlenir (sonra değişmezler; firmadan rol kalksa da geçmiş vekâlet bozulmaz).
        if (isNew && errors.Count == 0)
        {
            await CheckRoleAsync(entity.ImporterPartyId, WarehouseModule.RelationshipRoles.Importer, "importerPartyId",
                "Seçilen firma \"İthalatçı / malik\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin.", errors, cancellationToken);
            await CheckRoleAsync(entity.BrokerPartyId, WarehouseModule.RelationshipRoles.Broker, "brokerPartyId",
                "Seçilen firma \"Gümrük müşaviri\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin.", errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private async Task CheckRoleAsync(Guid partyId, string role, string field, string message, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        var stored = await Context.Data.Set<Party>().AsNoTracking()
            .Where(p => p.Id == partyId).Select(p => p.RelationshipRoles).SingleOrDefaultAsync(cancellationToken);
        if (!(stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(role))
        {
            errors.Add(new RecordFieldError(field, message));
        }
    }
}
