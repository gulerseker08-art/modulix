using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

public enum ReportIssueCode
{
    UnknownDataset = 1,

    /// <summary>Sütun veri kümesinde (artık) yok: özel alan arşivlendi ya da modül sütunu kaldırdı. Rapor çalışmaz, ne olduğu söylenir.</summary>
    ColumnRemoved = 2,
    UnknownColumn = 3,

    /// <summary>Sütun hassas alan: çağıranın izni yok. Süzgeçte, gruplamada ve toplamda da kullanılamaz.</summary>
    NotPermitted = 4,
    InvalidOperator = 5,
    InvalidValue = 6,
    InvalidGrouping = 7,
    InvalidMeasure = 8,
    InvalidSort = 9,
    InvalidChart = 10,
    TooMany = 11,
    ModuleDisabled = 12,
    NoAccess = 13,

    /// <summary>Okuma satır sınırına takıldı: sonuç eksik olabilir.</summary>
    Truncated = 14,

    /// <summary>Rapor eski bir veri kümesi sürümüyle kaydedilmiş.</summary>
    VersionDrift = 15,

    /// <summary>Bazı kayıtlarda ölçü değeri ya da para birimi yok; toplamlara girmedi.</summary>
    ExcludedRows = 16,
}

public sealed record ReportIssue(ReportIssueCode Code, string? Field, string Message, bool Blocking = true);

public enum ReportRunStatus
{
    Ok = 1,

    /// <summary>Tanım çalıştırılamaz (sütun yok, geçersiz süzgeç...). Veri okunmadı.</summary>
    Invalid = 2,

    /// <summary>Çağıran kayıt türünü okuyamıyor ya da hassas sütunlar için izni yok.</summary>
    Forbidden = 3,
    ModuleDisabled = 4,
    UnknownDataset = 5,
}

public enum ReportColumnRole
{
    Group = 1,
    Currency = 2,
    Measure = 3,
    Value = 4,
}

/// <summary>Sonuç sütunu. <see cref="Key"/> ölçü için "count" / "sum:alan", diğerleri için alan adıdır (sıralama bunu kullanır).</summary>
public sealed record ReportResultColumn(string Key, string Label, DatasetColumnKind Kind, ReportColumnRole Role, int ExcludedRows = 0);

/// <summary>
/// Sonuç satırı. <see cref="Cells"/> sütun sırasıyla görüntülenecek değerlerdir (ölçüler sayı, tarih "yyyy-MM-dd", seçim etiketi...).
/// <see cref="Keys"/> özet satırında grup anahtarlarıdır (ayrıntıya inmek için; boş anahtar boş gruptur), tablo satırında boştur.
/// </summary>
public sealed record ReportResultRow(IReadOnlyList<object?> Cells, IReadOnlyList<string?> Keys, string? Currency = null, Guid? RecordId = null);

/// <summary>Çalıştırma sonucu. Toplamlar ve satırlar aynı süzülmüş kayıt kümesinden hesaplanır (kart = detay).</summary>
public sealed record ReportResult(
    ReportRunStatus Status,
    bool IsSummary,
    IReadOnlyList<ReportResultColumn> Columns,
    IReadOnlyList<ReportResultRow> Rows,
    IReadOnlyList<ReportResultRow> Totals,
    int RowsRead,
    int RowsMatched,
    bool Truncated,
    bool RowsLimited,
    int Page,
    int PageSize,
    DateTimeOffset GeneratedAt,
    int DatasetVersion,
    IReadOnlyList<ReportIssue> Issues,
    string? DatasetName = null,
    string? RecordPath = null,
    ChartKind Chart = ChartKind.None)
{
    public static ReportResult Failed(ReportRunStatus status, IReadOnlyList<ReportIssue> issues, DateTimeOffset now, string? datasetName = null, int datasetVersion = 0) =>
        new(status, false, [], [], [], 0, 0, false, false, 1, 0, now, datasetVersion, issues, datasetName);
}

/// <summary>Ayrıntı: özet raporda bir satırın grup anahtarları (sütun sırasıyla) ve para birimi. Süzgeçlere eklenir; sonuç tablo satırlarıdır.</summary>
public sealed record ReportDrill(IReadOnlyList<string?> Keys, string? Currency);

/// <param name="StoredDatasetVersion">Rapor tanımı kaydedilirken veri kümesinin sürümü (kayıtlı rapor çalıştırılıyorsa); eskiyse sonuç uyarı taşır.</param>
/// <param name="ForExport">Dışa aktarma: tablo raporunda sayfalama yerine okunan bütün satırlar (en çok <see cref="ReportLimits.MaxRowsRead"/>) döner.</param>
public sealed record ReportRunRequest(
    ReportSpec Spec, ReportDrill? Drill = null, int Page = 1, int PageSize = ReportLimits.DefaultPageSize, int? StoredDatasetVersion = null, bool ForExport = false);

public static class ReportLimits
{
    /// <summary>Bir çalıştırmada kayıt boru hattından en fazla okunan kayıt; aşılırsa sonuç "kesildi" olarak işaretlenir.</summary>
    public const int MaxRowsRead = 5000;

    public const int MaxFilters = 10;
    public const int MaxGroups = 2;
    public const int MaxMeasures = 5;
    public const int MaxColumns = 20;
    public const int MaxInValues = 50;
    public const int MaxValueLength = 200;

    /// <summary>Özet raporda en fazla satır (grup sayısı).</summary>
    public const int MaxSummaryRows = 200;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
}
