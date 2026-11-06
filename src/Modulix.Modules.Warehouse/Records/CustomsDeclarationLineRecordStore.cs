using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Beyanname satırı (K6, K10, K17): yasal çekirdek alanlar (GTİP, kap cinsi ve adedi, brüt/net kilo, menşe, miktar ve birim) oluştururken
/// zorunludur ve liste alanları sessizce varsayılana düşmez. Net kilo brüt kilodan büyük olamaz, satır numarası beyanname içinde
/// benzersizdir, GTİP yalnızca rakamdır. İptal kategorisindeki beyannamenin satırları değiştirilemez. GTİP'in katalogda bulunması
/// engel değil uyarıdır (K9; formda gösterilir).
/// </summary>
internal sealed class CustomsDeclarationLineRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<CustomsDeclarationLine>(context)
{
    private static readonly string[] RequiredLists = ["containerTypeKey", "originCountryKey", "unitKey"];

    public override string EntityKey => WarehouseModule.Entities.CustomsDeclarationLine;

    protected override string ReadPermission => WarehouseModule.Permissions.DeclarationsRead;

    protected override string WritePermission => WarehouseModule.Permissions.DeclarationsWrite;

    protected override IReadOnlyList<RecordField<CustomsDeclarationLine>> Fields { get; } =
    [
        Field.Reference<CustomsDeclarationLine, CustomsDeclaration>("declarationId", "Beyanname bulunamadı.", l => l.DeclarationId, (l, v) => l.DeclarationId = v!.Value, required: true, createOnly: true),
        Field.Integer<CustomsDeclarationLine>("lineNo", l => l.LineNo, (l, v) => l.LineNo = v!.Value, min: 1, max: 9999, required: true),
        // Biçim ve uzunluk denetimi AppliedAsync'te (normalleştirme ve ileti tek yerde); burada yalnızca metin sınırı.
        Field.Text<CustomsDeclarationLine>("gtip", 40, l => l.Gtip, (l, v) => l.Gtip = v!, required: true),
        Field.Text<CustomsDeclarationLine>("description", CustomsDeclarationLine.DescriptionMaxLength, l => l.Description, (l, v) => l.Description = v!, required: true),
        Field.ListChoice<CustomsDeclarationLine>("containerTypeKey", WarehouseModule.Lists.ContainerType, l => l.ContainerTypeKey, (l, v) => l.ContainerTypeKey = v ?? string.Empty),
        Field.Integer<CustomsDeclarationLine>("containerCount", l => l.ContainerCount, (l, v) => l.ContainerCount = v!.Value, min: 1, max: 9_999_999, required: true),
        Field.Decimal<CustomsDeclarationLine>("grossKg", l => l.GrossKg, (l, v) => l.GrossKg = v!.Value, scale: 3, min: 0.001m, max: 999_999_999_999m, required: true),
        Field.Decimal<CustomsDeclarationLine>("netKg", l => l.NetKg, (l, v) => l.NetKg = v!.Value, scale: 3, min: 0m, max: 999_999_999_999m, required: true),
        Field.ListChoice<CustomsDeclarationLine>("originCountryKey", WarehouseModule.Lists.OriginCountry, l => l.OriginCountryKey, (l, v) => l.OriginCountryKey = v ?? string.Empty),
        Field.Decimal<CustomsDeclarationLine>("quantity", l => l.Quantity, (l, v) => l.Quantity = v!.Value, scale: 3, min: 0.001m, max: 999_999_999_999m, required: true),
        Field.ListChoice<CustomsDeclarationLine>("unitKey", WarehouseModule.Lists.DeclarationLineUnit, l => l.UnitKey, (l, v) => l.UnitKey = v ?? string.Empty),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CustomsDeclarationLine>> Filters { get; } = new Dictionary<string, RecordFilter<CustomsDeclarationLine>>
    {
        ["declarationId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.DeclarationId == id) : null,
        ["originCountryKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(l => l.OriginCountryKey == v) : null,
        // "gtip": kodun bu rakamlarla başladığı satırlar (fasıl veya pozisyon ön eki).
        ["gtip"] = (q, v) => v.Length is > 0 and <= GtipRules.MaxLength && v.All(char.IsAsciiDigit) ? q.Where(l => l.Gtip.StartsWith(v)) : null,
    };

    protected override IOrderedQueryable<CustomsDeclarationLine> Order(IQueryable<CustomsDeclarationLine> source) =>
        source.OrderBy(l => l.DeclarationId).ThenBy(l => l.LineNo);

    protected override IQueryable<CustomsDeclarationLine> Search(IQueryable<CustomsDeclarationLine> source, string term) =>
        source.Where(l => l.Gtip.Contains(term) || l.Description.Contains(term));

    protected override async Task AppliedAsync(CustomsDeclarationLine entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        // Yasal çekirdek liste alanları (kap cinsi, menşe, birim) yeni satırda açıkça seçilir; sessizce listenin ilk değerine düşmez (K17).
        if (isNew)
        {
            foreach (var name in RequiredLists.Where(n => values[n] is not JsonValue { } v || string.IsNullOrWhiteSpace(v.ToString())))
            {
                errors.Add(new RecordFieldError(name, "Bu alan zorunludur."));
            }
        }

        var (gtip, gtipError) = GtipRules.Normalize(entity.Gtip);
        if (gtipError is not null || gtip is null)
        {
            errors.Add(new RecordFieldError("gtip", gtipError ?? "GTİP zorunludur."));
        }
        else
        {
            entity.Gtip = gtip;
        }

        if (!DeclarationLineRules.NetWithinGross(entity.GrossKg, entity.NetKg))
        {
            errors.Add(new RecordFieldError("netKg", "Net kilo brüt kilodan büyük olamaz."));
        }

        var declaration = await Context.Data.Set<CustomsDeclaration>().AsNoTracking().SingleAsync(d => d.Id == entity.DeclarationId, cancellationToken);
        if (await DeclarationRules.IsCancelledAsync(Context, declaration, cancellationToken))
        {
            errors.Add(new RecordFieldError("declarationId", "İptal edilmiş beyannamenin satırları değiştirilemez."));
        }

        if (await Context.Data.Set<CustomsDeclarationLine>().AsNoTracking()
            .AnyAsync(l => l.DeclarationId == entity.DeclarationId && l.LineNo == entity.LineNo && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("lineNo", "Bu satır numarası beyannamede zaten var."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(CustomsDeclarationLine entity, CancellationToken cancellationToken)
    {
        var declaration = await Context.Data.Set<CustomsDeclaration>().AsNoTracking().SingleAsync(d => d.Id == entity.DeclarationId, cancellationToken);
        if (await DeclarationRules.IsCancelledAsync(Context, declaration, cancellationToken))
        {
            throw new RecordConflictException("İptal edilmiş beyannamenin satırları silinemez.");
        }
    }
}
