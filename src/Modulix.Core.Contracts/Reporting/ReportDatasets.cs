namespace Modulix.Core.Contracts.Reporting;

// Ortak rapor merkezinin sözleşmeleri (ADR-0035 K1, dilim 3). Modül, raporlanabilir bir kayıt türünü veri kümesi olarak bildirir;
// Core yalnızca bu beyanı ve kayıt boru hattını kullanır, modülün tablolarını ya da iş mantığını bilmez. Serbest SQL, kullanıcı kodu ve
// veri kümeleri arası birleştirme yoktur: bir rapor tek bir veri kümesinin bildirilmiş sütunlarından kurulur.

public enum DatasetColumnKind
{
    Text = 1,
    Number = 2,

    /// <summary>Tutar; <see cref="DatasetColumn.CurrencyField"/> ile eşleşir. Para birimleri hiçbir zaman birbirine toplanmaz.</summary>
    Money = 3,
    Date = 4,
    DateTime = 5,
    Flag = 6,
    Choice = 7,
    Lookup = 8,

    /// <summary>Kullanıcı (kimlik); etiket firmanın etkin üyesinin adıdır. Katalog seçenek olarak etkin üyeleri verir.</summary>
    User = 9,
}

[Flags]
public enum ColumnUse
{
    None = 0,

    /// <summary>Gruplanabilir (boyut).</summary>
    Group = 1,

    /// <summary>Toplanabilir (ölçü): sayı ve tutar sütunları.</summary>
    Measure = 2,

    /// <summary>Süzgeçte kullanılabilir.</summary>
    Filter = 4,

    /// <summary>Tablo sütunu olarak gösterilebilir.</summary>
    Show = 8,
}

/// <summary>Sabit seçenek (ör. fırsat durumu); firmanın düzenlediği listeler için <see cref="DatasetColumn.ChoiceList"/> kullanılır.</summary>
public sealed record DatasetOption(string Key, string Label);

/// <summary>
/// Veri kümesinin bildirilmiş sütunu. <see cref="Field"/> kayıt boru hattının alan adıdır ve rapor tanımında kalıcı kimlik olarak saklanır:
/// görünen ad değişse de rapor bozulmaz. Hassas alanlar ayrıca <see cref="Modules.SensitiveFields"/> ile bildirilir; izni olmayan çağıran
/// için sütun hiç görünmez, süzgeçte, gruplamada ya da toplamda kullanılamaz.
/// </summary>
public sealed record DatasetColumn(string Field, string Label, DatasetColumnKind Kind, ColumnUse Use)
{
    /// <summary>Tutar sütununun para birimi alanı (aynı kayıtta); tutar toplamları para birimine göre ayrılır.</summary>
    public string? CurrencyField { get; init; }

    /// <summary>Choice: <see cref="Customization.IChoiceLists"/> liste anahtarı (firmanın etiketleri). Verilmezse <see cref="Options"/> kullanılır.</summary>
    public string? ChoiceList { get; init; }

    public IReadOnlyList<DatasetOption>? Options { get; init; }

    /// <summary>Lookup: lookup tanımının anahtarı; etiket buradan çözülür (çağıranın görebildiği ölçüde).</summary>
    public string? LookupKey { get; init; }

    /// <summary>
    /// Eşitlik süzgecinin kayıt deposundaki adı (ör. "status"). Verilirse eşitlik süzgeci depoda uygulanır: okuma satır sınırına takılmadan
    /// daralır. Süzgeç her durumda bellekte de uygulanır; bu yalnızca bir hızlandırmadır.
    /// </summary>
    public string? StoreFilter { get; init; }

    /// <summary>Tarih sütununun "şundan itibaren / şuna kadar" (dahil) depo süzgeci adları; <see cref="StoreFilter"/> gibi yalnızca hızlandırmadır.</summary>
    public string? StoreFilterFrom { get; init; }

    public string? StoreFilterTo { get; init; }
}

/// <summary>Modülün hazır raporu: kullanıcı çalıştırır ya da kopyalayıp düzenler. Yetkiyi belirleyen veri kümesinin kayıt türüdür.</summary>
public sealed record ReadyReport(string Key, string Title, string Description, ReportSpec Spec);

/// <summary>
/// Raporlanabilir veri kümesi. <see cref="EntityKey"/> kayıt boru hattında okunan kayıt türüdür; izin, veri kapsamı ve hassas alan
/// kuralları o türün kurallarıdır. <see cref="Version"/> sütun anlamı değişince artar (rapor tanımı hangi sürümle kaydedildiğini bilir).
/// </summary>
public sealed record DatasetDefinition(string Key, string DisplayName, string EntityKey, int Version, IReadOnlyList<DatasetColumn> Columns)
{
    /// <summary>Firmanın özel alanları (Y9a) sütun olarak eklenebilir mi? Kayıt türü <see cref="Modules.CustomizableEntity"/> olmalıdır.</summary>
    public bool AllowsCustomFields { get; init; }

    /// <summary>Tablo raporunda varsayılan sütunlar (yeni rapor bunlarla başlar).</summary>
    public IReadOnlyList<string> DefaultColumns { get; init; } = [];

    /// <summary>Satırdan kaynak kayda geçiş adresi (uygulama köküne göre); <c>{id}</c> kayıt kimliğiyle değişir.</summary>
    public string? RecordPath { get; init; }

    public IReadOnlyList<ReadyReport> ReadyReports { get; init; } = [];
}

/// <summary>Rapor merkezinde hazır rapor gibi duran, modülün kendi ekranına giden bağlantı (ör. Satış görünümü). Görmek için izin gerekir.</summary>
public sealed record ReportLink(string Key, string Title, string Description, string Path, string Permission);

/// <summary>Modülün rapor merkezine katkısı. Bildirmeyen modül rapor merkezinde yer almaz.</summary>
public sealed record ModuleReporting(IReadOnlyList<DatasetDefinition> Datasets, IReadOnlyList<ReportLink> Links)
{
    public static ModuleReporting None { get; } = new([], []);
}

public enum FilterOperator
{
    Equals = 1,
    NotEquals = 2,
    Greater = 3,
    GreaterOrEqual = 4,
    Less = 5,
    LessOrEqual = 6,
    Contains = 7,
    In = 8,
    IsEmpty = 9,
    NotEmpty = 10,
}

public enum Aggregation
{
    Count = 1,
    Sum = 2,
    Average = 3,
    Min = 4,
    Max = 5,
}

/// <summary>Tarih gruplamasının ayrıntısı. Hafta/çeyrek yoktur: çeyrek mali takvime bağlıdır ve CRM satış raporlarındadır.</summary>
public enum DateGrain
{
    Day = 1,
    Month = 2,
    Year = 3,
}

public enum ChartKind
{
    None = 0,
    Bar = 1,
    Line = 2,
    Pie = 3,
}

public enum SortDirection
{
    Ascending = 1,
    Descending = 2,
}

/// <summary>Süzgeç: <see cref="Value"/> tek değer, <see cref="Values"/> yalnızca <see cref="FilterOperator.In"/> için. Tarih "yyyy-MM-dd", sayı değişmez kültür.</summary>
public sealed record ReportFilter(string Field, FilterOperator Operator, string? Value = null, IReadOnlyList<string>? Values = null);

/// <summary>Gruplama sütunu; tarih sütunlarında <see cref="Grain"/> verilir (verilmezse ay).</summary>
public sealed record ReportGroup(string Field, DateGrain? Grain = null);

/// <summary>Ölçü. <see cref="Aggregation.Count"/> sütunsuzdur (<see cref="Field"/> boş: kayıt sayısı).</summary>
public sealed record ReportMeasure(string? Field, Aggregation Aggregation);

public sealed record ReportSort(string Field, SortDirection Direction = SortDirection.Ascending);

/// <summary>
/// Rapor tanımı: sınırlı oluşturucunun ürettiği, veri kümesinin bildirilmiş sütunlarıyla sınırlı bildirim. Gruplama ve ölçü yoksa tablo
/// raporu (<see cref="Columns"/>), varsa özet raporudur. Tanım veri erişimi vermez; her çalıştırma çağıranın yetkisiyle yapılır.
/// </summary>
public sealed record ReportSpec(
    string Dataset,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ReportFilter> Filters,
    IReadOnlyList<ReportGroup> Groups,
    IReadOnlyList<ReportMeasure> Measures,
    ReportSort? Sort = null,
    ChartKind Chart = ChartKind.None)
{
    public bool IsSummary => Groups.Count > 0 || Measures.Count > 0;
}
