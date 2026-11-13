using System.Globalization;
using System.Text;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customs;
using Modulix.Core.Reporting;

namespace Modulix.Core.Imports;

/// <summary>İçe aktarma sınırları (ADR-0033). Pilot ölçümüyle gözden geçirilir.</summary>
public static class ImportLimits
{
    public const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>Dosya koşu boyunca bellekte tutulur ve kaldığı yerden devam yoktur; kalıcı iş kuyruğu gelince yükseltilir.</summary>
    public const int MaxRows = 2000;

    public const int MaxColumns = 200;
}

/// <param name="RowNo">Dosyadaki satır numarası (başlık 1'dir). Boş satırlar numarayı artırır, böylece kullanıcı dosyasındaki yeri bulur.</param>
public sealed record ImportFileRow(int RowNo, IReadOnlyList<string?> Cells);

public sealed record ImportFileContent(char Delimiter, IReadOnlyList<string> Headers, IReadOnlyList<ImportFileRow> Rows);

/// <summary>Dosya okunamadı (bozuk ya da desteklenmeyen biçim, sınır aşımı). İleti kullanıcıya gösterilir.</summary>
public sealed class ImportFileException(string message) : Exception(message);

/// <summary>Çalışma kitabı (.xlsx) okuyucu sözleşmesi (ADR-0033, Dilim 4a). Gerçeklemesi altyapıdadır; Core paket bağımlılığı taşımaz.</summary>
public interface IImportWorkbookReader
{
    /// <summary>İlk sayfayı hücre satırlarına indirir. Okunamayan dosyada <see cref="ImportFileException"/> fırlatır.</summary>
    ImportFileContent Read(byte[] content);
}

/// <summary>Dosya biçimini uzantıdan seçer; satır kurma ve hücre temizliği CSV ve XLSX için ortaktır.</summary>
public static class ImportFiles
{
    public static bool IsSupported(string fileName) => Format(fileName) is not null;

    public static bool IsWorkbook(string fileName) => Format(fileName) == ".xlsx";

    private static string? Format(string fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() is ".csv" or ".txt" or ".xlsx" ? Path.GetExtension(fileName!).ToLowerInvariant() : null;

    /// <summary>Boş satırları atlar (numara yine ilerler), ilk dolu satırı başlık sayar; hücreleri kırpar, hata raporunun kaçışını geri alır.</summary>
    public static ImportFileContent Build(char delimiter, IEnumerable<IReadOnlyList<object?>> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        List<string>? headers = null;
        var rows = new List<ImportFileRow>();
        var rowNo = 0;
        foreach (var line in raw)
        {
            rowNo++;
            var cells = line.Select(Clean).ToList();
            if (cells.All(c => c is null))
            {
                continue;
            }

            if (headers is null)
            {
                headers = [.. cells.Select(c => c ?? string.Empty)];
            }
            else
            {
                rows.Add(new ImportFileRow(rowNo, cells));
                if (rows.Count > ImportLimits.MaxRows)
                {
                    // Sınırı aşan dosya zaten reddedilecek; kalanını okumanın anlamı yok (büyük .xlsx belleği doldurmasın).
                    break;
                }
            }
        }

        return new ImportFileContent(delimiter, headers ?? [], rows);
    }

    /// <summary>Hücre değerini içe aktarma metnine çevirir: sayı ve tarih, metin sütunlarının okuduğu Türkçe biçime (1234,5 ve gg.aa.yyyy) iner.</summary>
    public static string? Text(object? cell) => cell switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "Evet" : "Hayır",
        DateTime date => date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : date.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        double number => Number(double.IsFinite(number) ? (decimal)Math.Round(number, 10) : 0m),
        float number => Number(float.IsFinite(number) ? (decimal)Math.Round(number, 6) : 0m),
        decimal number => Number(number),
        IFormattable other => other.ToString(null, CultureInfo.InvariantCulture),
        _ => cell.ToString(),
    };

    private static string Number(decimal number) => number.ToString("0.##########", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string? Clean(object? cell) =>
        Text(cell)?.Trim() is { Length: > 0 } value ? ReportCsv.Unescape(value) : null;
}

/// <summary>CSV dosyasını başlık ve satırlara indirir. Boş satırlar atlanır; hücreler kırpılır, hata raporunun kaçışı geri alınır.</summary>
public static class ImportCsvFile
{
    public static ImportFileContent Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var delimiter = GtipCsv.DetectDelimiter(GtipCsv.Decode(content));
        return ImportFiles.Build(delimiter, GtipCsv.Parse(content));
    }
}

/// <summary>Dosya sütununun bağlandığı alan.</summary>
public sealed record ImportColumn(int Index, ImportField Field);

/// <param name="Unknown">Hiçbir alanla eşleşmeyen başlıklar; yok sayılır ve uyarı yazılır.</param>
/// <param name="Errors">Dosyayı işlemeyi engelleyen sorunlar (eksik zorunlu sütun, tekrar eden sütun).</param>
public sealed record ImportColumnMap(IReadOnlyList<ImportColumn> Columns, IReadOnlyList<string> Unknown, IReadOnlyList<string> Errors);

/// <summary>Başlıkları alanlara bağlar: başlık alanın görünen adı ya da teknik anahtarıdır, büyük/küçük harf ve kenar boşlukları önemsizdir.</summary>
public static class ImportColumns
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static ImportColumnMap Bind(IReadOnlyList<string> headers, IReadOnlyList<ImportField> fields)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(fields);
        var byName = new Dictionary<string, ImportField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            byName.TryAdd(Name(field.Label), field);
            byName.TryAdd(Name(field.Key), field);
        }

        // Kayıtlı eşleme (Dilim 4c) etiket ve anahtardan sonra eklenir: bir alanın adı hiçbir zaman başka alanın takma adına kaptırılmaz.
        foreach (var field in fields)
        {
            foreach (var alias in field.Aliases)
            {
                byName.TryAdd(Name(alias), field);
            }
        }

        var columns = new List<ImportColumn>();
        var unknown = new List<string>();
        var errors = new List<string>();
        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index].Trim();
            if (header.Length == 0 || Name(header) == Name(ImportErrorReport.ErrorColumn))
            {
                continue;
            }

            if (!byName.TryGetValue(Name(header), out var field))
            {
                unknown.Add(header);
            }
            else if (columns.Any(c => c.Field.Key == field.Key))
            {
                errors.Add($"'{field.Label}' sütunu dosyada birden çok kez var.");
            }
            else
            {
                columns.Add(new ImportColumn(index, field));
            }
        }

        errors.AddRange(fields.Where(f => f.Required && columns.All(c => c.Field.Key != f.Key)).Select(f => $"Zorunlu sütun yok: {f.Label}."));
        return new ImportColumnMap(columns, unknown, errors);
    }

    internal static string Name(string value) => value.Trim().ToLower(Turkish);
}

/// <summary>Şablon ve hata raporu dosyaları: Türkçe Excel'in doğrudan açtığı biçimde (UTF-8 BOM, CRLF).</summary>
public static class ImportErrorReport
{
    /// <summary>Hata raporunda eklenen sütun; rapor düzeltilip yeniden yüklenince yok sayılır.</summary>
    public const string ErrorColumn = "Hata";

    private const char TemplateDelimiter = ';';

    /// <summary>Boş şablon: yalnızca başlık satırı.</summary>
    public static byte[] Template(IReadOnlyList<ImportField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var builder = new StringBuilder();
        AppendLine(builder, TemplateDelimiter, fields.Select(f => f.Label));
        return Encode(builder);
    }

    /// <summary>Hatalı satırlar dosyadaki haliyle ve hata açıklamasıyla, kaynak dosyanın ayracıyla. Hücreler formül enjeksiyonuna karşı kaçışlanır.</summary>
    public static byte[] Build(ImportFileContent file, IReadOnlyList<(ImportFileRow Row, string Error)> failed)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(failed);
        var builder = new StringBuilder();
        AppendLine(builder, file.Delimiter, [.. file.Headers, ErrorColumn]);
        foreach (var (row, error) in failed)
        {
            var cells = Enumerable.Range(0, file.Headers.Count).Select(i => i < row.Cells.Count ? row.Cells[i] ?? string.Empty : string.Empty);
            AppendLine(builder, file.Delimiter, [.. cells, error]);
        }

        return Encode(builder);
    }

    private static void AppendLine(StringBuilder builder, char delimiter, IEnumerable<string> cells) =>
        builder.AppendJoin(delimiter, cells.Select(c => ReportCsv.Quote(ReportCsv.Text(c), delimiter))).Append("\r\n");

    private static byte[] Encode(StringBuilder builder) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
}
