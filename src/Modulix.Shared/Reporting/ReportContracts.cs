using System.Text.Json;

namespace Modulix.Shared.Reporting;

// Ortak rapor merkezi (/api/reports, ADR-0026 dilim 3). Rapor tanımı bir veri kümesinin bildirilmiş sütunlarından kurulur (serbest SQL ya da
// birleştirme yoktur) ve her çalıştırma çağıranın yetkisiyle yapılır. Sabit değerler küçük harfle başlar (camelCase):
//   işleç: equals, notEquals, greater, greaterOrEqual, less, lessOrEqual, contains, in, isEmpty, notEmpty
//   toplama: count, sum, average, min, max          tarih ayrıntısı: day, month, year
//   grafik: none, bar, line, pie                      sıralama yönü: ascending, descending
//   sütun türü: text, number, money, date, dateTime, flag, choice, lookup
//   sütun kullanımı (bayrak listesi): group, measure, filter, show

public static class ReportAccess
{
    public const string Read = "core.reports.read";
    public const string Manage = "core.reports.manage";
    public const string Share = "core.reports.share";
    public const string Export = "core.reports.export";
    public const string Schedule = "core.reports.schedule";
}

public sealed class ReportFilterDto
{
    public string Field { get; set; } = string.Empty;

    public string Operator { get; set; } = "equals";

    public string? Value { get; set; }

    public List<string>? Values { get; set; }
}

public sealed class ReportGroupDto
{
    public string Field { get; set; } = string.Empty;

    /// <summary>Yalnızca tarih sütunlarında: day, month, year. Boşsa ay.</summary>
    public string? Grain { get; set; }
}

public sealed class ReportMeasureDto
{
    /// <summary>Kayıt sayısı (count) için boş.</summary>
    public string? Field { get; set; }

    public string Aggregation { get; set; } = "count";
}

public sealed class ReportSortDto
{
    /// <summary>Özet raporda grup sütunu ya da ölçü kimliği ("count", "sum:amount"); tablo raporunda sütun.</summary>
    public string Field { get; set; } = string.Empty;

    public string Direction { get; set; } = "ascending";
}

/// <summary>Rapor tanımı. Gruplama ve ölçü yoksa tablo raporu (<see cref="Columns"/>), varsa özet raporudur.</summary>
public sealed class ReportSpecDto
{
    public string Dataset { get; set; } = string.Empty;

    public List<string> Columns { get; set; } = [];

    public List<ReportFilterDto> Filters { get; set; } = [];

    public List<ReportGroupDto> Groups { get; set; } = [];

    public List<ReportMeasureDto> Measures { get; set; } = [];

    public ReportSortDto? Sort { get; set; }

    public string Chart { get; set; } = "none";
}

// ---- Katalog

public sealed record ReportOptionDto(string Key, string Label);

public sealed record ReportColumnInfoDto(
    string Field, string Label, string Kind, IReadOnlyList<string> Use, bool Custom, string? CurrencyField, IReadOnlyList<ReportOptionDto>? Options);

public sealed record ReadyReportDto(string Key, string Title, string Description, ReportSpecDto Spec);

public sealed record ReportDatasetDto(
    string Key,
    string ModuleKey,
    string Name,
    int Version,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReportColumnInfoDto> Columns,
    IReadOnlyList<string> DefaultColumns,
    IReadOnlyList<ReadyReportDto> ReadyReports,
    string? RecordPath);

public sealed record ReportLinkDto(string Key, string ModuleKey, string Title, string Description, string Path);

public sealed record ReportCatalogDto(IReadOnlyList<ReportDatasetDto> Datasets, IReadOnlyList<ReportLinkDto> Links);

// ---- Kayıtlı raporlar

/// <summary>Sağlık: ok, datasetMissing, moduleDisabled, forbidden, columnsInvalid. Görünürlük: private, users, tenant.</summary>
public sealed record ReportSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string DatasetKey,
    string? DatasetName,
    string Visibility,
    Guid OwnerUserId,
    string? OwnerName,
    bool IsOwner,
    bool IsFavorite,
    int SharedWithCount,
    DateTimeOffset UpdatedAt,
    long Version,
    string Health,
    string? HealthMessage);

public sealed record ReportPageDto(IReadOnlyList<ReportSummaryDto> Items, int Page, int PageSize, int TotalCount);

public sealed record ReportDetailDto(ReportSummaryDto Summary, ReportSpecDto Spec, int DatasetVersion, IReadOnlyList<Guid> SharedWith);

public sealed record SaveReportRequest(string Name, string? Description, ReportSpecDto Spec, long? ExpectedVersion = null);

public sealed record ShareReportRequest(string Visibility, IReadOnlyList<Guid>? Users, long? ExpectedVersion = null);

public sealed record FavoriteReportRequest(bool Favorite);

// ---- Çalıştırma

/// <summary>Özet raporda bir satırın ayrıntısı: satırın grup anahtarları (sütun sırasıyla; boş grup null) ve para birimi.</summary>
public sealed record ReportDrillDto(IReadOnlyList<string?> Keys, string? Currency);

public sealed record RunReportRequest(ReportSpecDto? Spec, ReportDrillDto? Drill, int Page = 1, int PageSize = 50);

public sealed record ReportIssueDto(string Code, string? Field, string Message, bool Blocking);

public sealed record ReportResultColumnDto(string Key, string Label, string Kind, string Role, int ExcludedRows);

/// <summary>Hücreler sütun sırasıyla; değer yoksa null. <see cref="Keys"/> özet satırında ayrıntıya inmek içindir.</summary>
public sealed record ReportResultRowDto(IReadOnlyList<JsonElement> Cells, IReadOnlyList<string?> Keys, string? Currency, Guid? RecordId);

/// <summary>Durum: ok, invalid, forbidden, moduleDisabled, unknownDataset. Durum ok değilse veri okunmamıştır ve nedeni <see cref="Issues"/>'tadır.</summary>
public sealed record ReportResultDto(
    string Status,
    bool IsSummary,
    IReadOnlyList<ReportResultColumnDto> Columns,
    IReadOnlyList<ReportResultRowDto> Rows,
    IReadOnlyList<ReportResultRowDto> Totals,
    int RowsRead,
    int RowsMatched,
    bool Truncated,
    bool RowsLimited,
    int Page,
    int PageSize,
    DateTimeOffset GeneratedAt,
    int DatasetVersion,
    IReadOnlyList<ReportIssueDto> Issues,
    string? DatasetName,
    string? RecordPath,
    string Chart);

// ---- Dışa aktarma ve zamanlama (dilim 4)

/// <summary>Hazırlanmış CSV dosyası. Kaynak: manual, schedule. Süresi dolan dosya indirilemez; yeniden dışa aktarılır.</summary>
public sealed record ReportExportDto(
    Guid Id,
    Guid? ReportId,
    string ReportName,
    string DatasetKey,
    string FileName,
    long SizeBytes,
    int RowCount,
    bool Truncated,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int DownloadCount,
    bool Expired);

/// <summary>Kayıtsız tanımı (oluşturucudaki önizleme) dışa aktarır.</summary>
public sealed record ExportReportRequest(ReportSpecDto? Spec, string? Name);

/// <summary>Sıklık: daily, weekly, monthly. Haftanın günü: sunday..saturday (haftalıkta zorunlu). Ayın günü 1-28 (aylıkta zorunlu). Saat firmanın saat diliminde yerel saattir.</summary>
/// <summary>
/// Biçim: csv (varsayılan) ya da xlsx. <see cref="RecipientIds"/>: sahibi dışındaki alıcılar (dağıtım listesi, en çok 20, etkin üyeler); null güncellemede listeyi korur,
/// boş liste temizler. Her alıcı raporu <b>kendi yetkisiyle</b> üretilmiş kendi dosyası olarak alır; göremeyen ya da yetkisi olmayan alıcı atlanır.
/// </summary>
public sealed record SaveReportScheduleRequest(
    string Frequency, int Hour, string? DayOfWeek = null, int? DayOfMonth = null, long? ExpectedVersion = null, string? Format = null, IReadOnlyList<Guid>? RecipientIds = null);

/// <summary>Dağıtım listesindeki alıcı; etkin üye değilse <see cref="DisplayName"/> boştur.</summary>
public sealed record ReportScheduleRecipientDto(Guid UserId, string? DisplayName);

public sealed record SetReportScheduleEnabledRequest(bool Enabled);

/// <summary>Son durum: Delivered, Stopped, Failed. Durdurulduysa <see cref="StoppedReason"/> nedeni söyler (ör. yetki kaybı).</summary>
public sealed record ReportScheduleDto(
    Guid Id,
    Guid ReportId,
    string? ReportName,
    string Frequency,
    int Hour,
    string? DayOfWeek,
    int? DayOfMonth,
    bool Enabled,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    string? LastMessage,
    string? StoppedReason,
    long Version,
    string Format = "csv",
    IReadOnlyList<ReportScheduleRecipientDto>? Recipients = null);
