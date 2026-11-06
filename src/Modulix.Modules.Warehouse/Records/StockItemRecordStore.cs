using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Stok kartı kayıtları. Kod tenant içinde benzersizdir (büyük/küçük harf farkı yok sayılır). Birim firmanın birim listesinden gelir
/// (Y9b). GTİP varsayılan önerisidir: rakamlardan oluşur, metin saklanır (<see cref="GtipRules"/>); katalogda bulunmaması engellemez.
/// </summary>
internal sealed partial class StockItemRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<StockItem>(context)
{
    public override string EntityKey => WarehouseModule.Entities.StockItem;

    protected override string ReadPermission => WarehouseModule.Permissions.StockItemsRead;

    protected override string WritePermission => WarehouseModule.Permissions.StockItemsWrite;

    protected override IReadOnlyList<RecordField<StockItem>> Fields { get; } =
    [
        Field.Text<StockItem>("code", StockItem.CodeMaxLength, s => s.Code, (s, v) => s.Code = v!, required: true),
        Field.Text<StockItem>("name", StockItem.NameMaxLength, s => s.Name, (s, v) => s.Name = v!, required: true),
        Field.ListChoice<StockItem>("unitKey", WarehouseModule.Lists.StockUnit, s => s.UnitKey, (s, v) => s.UnitKey = v ?? string.Empty),
        // Biçim ve uzunluk denetimi AppliedAsync'te (normalleştirme ve ileti tek yerde); burada yalnızca metin sınırı.
        Field.Text<StockItem>("gtip", 40, s => s.Gtip, (s, v) => s.Gtip = v),
        Field.Text<StockItem>("description", StockItem.DescriptionMaxLength, s => s.Description, (s, v) => s.Description = v),
        Field.Flag<StockItem>("isActive", s => s.IsActive, (s, v) => s.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<StockItem>> Filters { get; } = new Dictionary<string, RecordFilter<StockItem>>
    {
        ["unitKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(s => s.UnitKey == v) : null,
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(s => s.IsActive == flag) : null,
        // "gtip": kodun bu rakamlarla başladığı kartlar (ör. fasıl veya pozisyon ön eki).
        ["gtip"] = (q, v) => v.Length is > 0 and <= GtipRules.MaxLength && v.All(char.IsAsciiDigit) ? q.Where(s => s.Gtip != null && s.Gtip.StartsWith(v)) : null,
    };

    protected override IOrderedQueryable<StockItem> Order(IQueryable<StockItem> source) => source.OrderBy(s => s.Code);

    // Liste başlıklarına göre sıralama (sort=code, sort=-code ...); eşit kayıtlar koda göre sıralanır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("code", "name", "unitKey", "gtip", "isActive", "createdAt");

    protected override IOrderedQueryable<StockItem> Order(IQueryable<StockItem> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "code" => By(source, s => s.Code, desc),
            "name" => By(source, s => s.Name, desc).ThenBy(s => s.Code),
            "unitKey" => By(source, s => s.UnitKey, desc).ThenBy(s => s.Code),
            "gtip" => ByNullsLast(source, s => s.Gtip, desc).ThenBy(s => s.Code),
            "isActive" => By(source, s => s.IsActive, desc).ThenBy(s => s.Code),
            "createdAt" => By(source, s => s.CreatedAt, desc).ThenBy(s => s.Code),
            _ => Order(source),
        };
    }

    protected override IQueryable<StockItem> Search(IQueryable<StockItem> source, string term) =>
        source.Where(s => s.Code.Contains(term) || s.Name.Contains(term) || (s.Gtip != null && s.Gtip.Contains(term)));

    protected override async Task AppliedAsync(StockItem entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (!CodePattern().IsMatch(entity.Code))
        {
            errors.Add(new RecordFieldError("code", "Kod yalnızca harf, rakam ve - _ . / karakterlerini içerebilir."));
        }
        else if (await Context.Data.Set<StockItem>().AsNoTracking().AnyAsync(s => s.Code == entity.Code && s.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu kod başka bir stok kartında kullanılıyor."));
        }

        var (gtip, gtipError) = GtipRules.Normalize(entity.Gtip);
        if (gtipError is not null)
        {
            errors.Add(new RecordFieldError("gtip", gtipError));
        }
        else
        {
            entity.Gtip = gtip;
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]*$")]
    private static partial Regex CodePattern();
}
