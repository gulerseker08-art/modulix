using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>Beyanname durumu kategorisine bağlı kurallar (K14, K18): kod durumun adına değil kategorisine bakar.</summary>
internal static class DeclarationRules
{
    /// <summary>Durum anahtarının kategorisi (tenant'ın listesine göre; pasif durumlar dahil).</summary>
    public static async Task<string?> CategoryAsync(WarehouseStoreContext context, string statusKey, CancellationToken cancellationToken)
    {
        var list = await context.ChoiceLists.GetAsync(WarehouseModule.Lists.DeclarationStatus, cancellationToken);
        return list.Find(statusKey)?.Category;
    }

    public static async Task<bool> IsCancelledAsync(WarehouseStoreContext context, CustomsDeclaration declaration, CancellationToken cancellationToken) =>
        await CategoryAsync(context, declaration.StatusKey, cancellationToken) == WarehouseModule.DeclarationStatusCategories.Cancelled;
}

/// <summary>
/// Beyanname başlığı (K6). Tescil numarası tenant içinde benzersizdir (büyük/küçük harf farkı yok sayılır); biçim ve uzunluk kuralı
/// rejim profilindedir (K7, sonraki adım), burada yalnızca harf, rakam ve <c>- _ . /</c> denetlenir. İthalatçı "ithalatçı / malik",
/// müşavir "gümrük müşaviri" ilişki rolüne sahip olmalıdır (taraf seçilirken ya da değiştirilirken denetlenir). Satırı olan beyanname
/// silinemez.
/// </summary>
internal sealed partial class CustomsDeclarationRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<CustomsDeclaration>(context)
{
    public override string EntityKey => WarehouseModule.Entities.CustomsDeclaration;

    protected override string ReadPermission => WarehouseModule.Permissions.DeclarationsRead;

    protected override string WritePermission => WarehouseModule.Permissions.DeclarationsWrite;

    protected override IReadOnlyList<RecordField<CustomsDeclaration>> Fields { get; } =
    [
        Field.Text<CustomsDeclaration>("registrationNo", CustomsDeclaration.RegistrationNoMaxLength, d => d.RegistrationNo, (d, v) => d.RegistrationNo = v!, required: true, upperCase: true),
        Field.Date<CustomsDeclaration>("registrationDate", d => d.RegistrationDate, (d, v) => d.RegistrationDate = v!.Value, required: true),
        Field.Text<CustomsDeclaration>("customsOffice", CustomsDeclaration.OfficeMaxLength, d => d.CustomsOffice, (d, v) => d.CustomsOffice = v),
        Field.ListChoice<CustomsDeclaration>("regimeKey", WarehouseModule.Lists.CustomsRegime, d => d.RegimeKey, (d, v) => d.RegimeKey = v ?? string.Empty),
        Field.ListChoice<CustomsDeclaration>("statusKey", WarehouseModule.Lists.DeclarationStatus, d => d.StatusKey, (d, v) => d.StatusKey = v ?? string.Empty),
        Field.PartyRef<CustomsDeclaration>("importerPartyId", "İthalatçı bulunamadı.", d => d.ImporterPartyId, (d, v) => d.ImporterPartyId = v!.Value, required: true),
        Field.PartyRef<CustomsDeclaration>("brokerPartyId", "Gümrük müşaviri bulunamadı.", d => d.BrokerPartyId, (d, v) => d.BrokerPartyId = v),
        Field.Text<CustomsDeclaration>("externalReference", CustomsDeclaration.ExternalReferenceMaxLength, d => d.ExternalReference, (d, v) => d.ExternalReference = v),
        Field.Text<CustomsDeclaration>("description", CustomsDeclaration.DescriptionMaxLength, d => d.Description, (d, v) => d.Description = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CustomsDeclaration>> Filters { get; } = new Dictionary<string, RecordFilter<CustomsDeclaration>>
    {
        // Firma silme/birleştirme koruması bu iki süzgeçle kullanım sayar (SharedRecordReferences).
        ["importerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(d => d.ImporterPartyId == id) : null,
        ["brokerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(d => d.BrokerPartyId == id) : null,
        ["statusKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(d => d.StatusKey == v) : null,
        ["regimeKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(d => d.RegimeKey == v) : null,
        // Tescil tarihi aralığı (yyyy-AA-gg, uçlar dahil).
        ["registeredFrom"] = (q, v) => ParseDay(v) is { } day ? q.Where(d => d.RegistrationDate >= day) : null,
        ["registeredTo"] = (q, v) => ParseDay(v) is { } day ? q.Where(d => d.RegistrationDate <= day) : null,
    };

    protected override IOrderedQueryable<CustomsDeclaration> Order(IQueryable<CustomsDeclaration> source) =>
        source.OrderByDescending(d => d.RegistrationDate).ThenBy(d => d.RegistrationNo);

    // Firma adıyla arama yok: firma görünürlüğü (CRM kapsamı) bu ekrandan dolanılmasın; firma süzgeçleri seçim kutusuyla yapılır.
    protected override IQueryable<CustomsDeclaration> Search(IQueryable<CustomsDeclaration> source, string term) =>
        source.Where(d => d.RegistrationNo.Contains(term)
            || (d.ExternalReference != null && d.ExternalReference.Contains(term))
            || (d.CustomsOffice != null && d.CustomsOffice.Contains(term)));

    protected override async Task AppliedAsync(CustomsDeclaration entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (!RegistrationNoPattern().IsMatch(entity.RegistrationNo))
        {
            errors.Add(new RecordFieldError("registrationNo", "Tescil numarası yalnızca harf, rakam ve - _ . / karakterlerini içerebilir."));
        }
        else if (await Context.Data.Set<CustomsDeclaration>().AsNoTracking()
            .AnyAsync(d => d.RegistrationNo == entity.RegistrationNo && d.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("registrationNo", "Bu tescil numarası başka bir beyannamede kullanılıyor."));
        }

        if (entity.ExternalReference is { } reference
            && await Context.Data.Set<CustomsDeclaration>().AsNoTracking()
                .AnyAsync(d => d.ExternalReference == reference && d.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("externalReference", "Bu dış referans başka bir beyannamede kullanılıyor."));
        }

        if (entity.BrokerPartyId == entity.ImporterPartyId)
        {
            errors.Add(new RecordFieldError("brokerPartyId", "İthalatçı ve gümrük müşaviri aynı firma olamaz."));
        }

        // Rol, taraf ilk seçildiğinde ya da değiştirildiğinde denetlenir; firmadan rol sonradan kalksa da eski beyanname bozulmaz.
        if (errors.Count == 0)
        {
            var stored = isNew
                ? null
                : await Context.Data.Set<CustomsDeclaration>().AsNoTracking().Where(d => d.Id == entity.Id)
                    .Select(d => new { d.ImporterPartyId, d.BrokerPartyId }).SingleOrDefaultAsync(cancellationToken);
            if (stored is null || stored.ImporterPartyId != entity.ImporterPartyId)
            {
                await CheckRoleAsync(entity.ImporterPartyId, WarehouseModule.RelationshipRoles.Importer, "importerPartyId",
                    "Seçilen firma \"İthalatçı / malik\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin.", errors, cancellationToken);
            }

            if (entity.BrokerPartyId is { } broker && (stored is null || stored.BrokerPartyId != broker))
            {
                await CheckRoleAsync(broker, WarehouseModule.RelationshipRoles.Broker, "brokerPartyId",
                    "Seçilen firma \"Gümrük müşaviri\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin.", errors, cancellationToken);
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(CustomsDeclaration entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<CustomsDeclarationLine>().AsNoTracking().AnyAsync(l => l.DeclarationId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Beyannamenin satırları var; önce satırları silin.");
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

    private static DateOnly? ParseDay(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]*$")]
    private static partial Regex RegistrationNoPattern();
}
