using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Bakiye (ADR-0032 K13): bir partinin bir lokasyondaki miktarı. <b>Yalnızca okunur</b>: bakiye defterden türetilir, hareket kaydıyla aynı işlemde değişir
/// ve genel uçlardan oluşturulamaz, değiştirilemez, silinemez. Okuma hareket okuma iznine bağlıdır.
/// </summary>
internal sealed class StockBalanceRecordStore : WarehouseRecordStore<StockBalance>
{
    private const string Message = "Bakiye stok hareket defterinden türetilir; doğrudan değiştirilemez. Stok hareketi kaydedin.";

    private readonly IReadOnlyDictionary<string, RecordFilter<StockBalance>> filters;

    public StockBalanceRecordStore(WarehouseStoreContext context)
        : base(context)
    {
        filters = new Dictionary<string, RecordFilter<StockBalance>>
        {
            ["lotId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(b => b.LotId == id) : null,
            ["locationId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(b => b.LocationId == id) : null,
            // Bir stok kartının bütün partilerindeki bakiyeler.
            ["stockItemId"] = (q, v) => FilterValue.Id(v) is { } id
                ? q.Where(b => context.Data.Set<Lot>().Any(l => l.Id == b.LotId && l.StockItemId == id))
                : null,
            // Sıfır bakiyeli satırlar (tükenmiş) gizlenir.
            ["nonZero"] = (q, v) => FilterValue.Bool(v) is true ? q.Where(b => b.Quantity > 0) : FilterValue.Bool(v) is false ? q : null,
        };
    }

    public override string EntityKey => WarehouseModule.Entities.StockBalance;

    protected override string ReadPermission => WarehouseModule.Permissions.MovementsRead;

    protected override string WritePermission => WarehouseModule.Permissions.MovementsWrite;

    protected override string? ImmutableMessage => Message;

    protected override string? CreateBlockedMessage => Message;

    protected override IReadOnlyList<RecordField<StockBalance>> Fields { get; } =
    [
        Field.Computed<StockBalance>("lotId", b => b.LotId.ToString()),
        Field.Computed<StockBalance>("locationId", b => b.LocationId.ToString()),
        Field.Computed<StockBalance>("quantity", b => System.Text.Json.Nodes.JsonValue.Create(b.Quantity)),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<StockBalance>> Filters => filters;

    protected override IOrderedQueryable<StockBalance> Order(IQueryable<StockBalance> source) => source.OrderBy(b => b.LotId).ThenBy(b => b.LocationId);

    // Miktara göre sıralama (en çok/en az stok); eşit satırlar varsayılan sırada kalır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("quantity");

    protected override IOrderedQueryable<StockBalance> Order(IQueryable<StockBalance> source, string? sort) =>
        sort?.TrimStart('-') == "quantity" ? By(source, b => b.Quantity, sort.StartsWith('-')).ThenBy(b => b.LotId).ThenBy(b => b.LocationId) : Order(source);

    // Bakiyede metin alanı yok; arama kimlikle yapılmaz.
    protected override IQueryable<StockBalance> Search(IQueryable<StockBalance> source, string term) => source.Where(_ => false);
}
