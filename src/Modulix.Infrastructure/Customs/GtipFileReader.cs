using System.IO.Compression;
using MiniExcelLibs;
using Modulix.Core.Customs;

namespace Modulix.Infrastructure.Customs;

/// <summary>
/// GTİP dosyası okuyucu (ADR-0032 K9): <c>.xlsx</c> (ilk sayfa, MiniExcel ile satır satır) ve <c>.csv</c>/<c>.txt</c>. Eski ikili <c>.xls</c>
/// desteklenmez. Yorumlama <see cref="GtipImportRules"/>'tadır; burası yalnızca biçimi hücre listesine indirir. Okunamayan ya da
/// beklenmedik dosya hata fırlatmaz, <see cref="GtipFileReadResult.FatalError"/> döner.
/// </summary>
internal sealed class GtipFileReader : IGtipFileReader
{
    /// <summary>Açılmış (sıkıştırması çözülmüş) toplam boyut sınırı: küçük bir .xlsx'in belleği doldurmasını (zip bombası) keser.</summary>
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;

    public GtipFileReadResult Read(byte[] content, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            return GtipFileReadResult.Fatal("Dosya boş.");
        }

        if (content.Length > GtipImportRules.MaxFileBytes)
        {
            return GtipFileReadResult.Fatal($"Dosya en fazla {GtipImportRules.MaxFileBytes / (1024 * 1024)} MB olabilir.");
        }

        try
        {
            return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
            {
                ".xlsx" => ReadWorkbook(content),
                ".csv" or ".txt" => GtipImportRules.Interpret(GtipCsv.Parse(content)),
                ".xls" => GtipFileReadResult.Fatal("Eski .xls biçimi desteklenmiyor; dosyayı Excel'de '.xlsx' ya da 'CSV' olarak kaydedip yükleyin."),
                _ => GtipFileReadResult.Fatal("Desteklenen dosya biçimleri: .xlsx ve .csv."),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or OverflowException or System.Xml.XmlException or IOException or KeyNotFoundException)
        {
            return GtipFileReadResult.Fatal("Dosya okunamadı; geçerli bir Excel (.xlsx) ya da CSV dosyası olduğundan emin olun.");
        }
    }

    private static GtipFileReadResult ReadWorkbook(byte[] content)
    {
        // .xlsx bir zip dosyasıdır ("PK" ile başlar); metin dosyasının uzantısı değiştirilmişse anlaşılır bir ileti verilir.
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K')
        {
            return GtipFileReadResult.Fatal("Dosya geçerli bir .xlsx değil (Excel çalışma kitabı olarak kaydedilmemiş).");
        }

        using var stream = new MemoryStream(content, writable: false);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
            {
                return GtipFileReadResult.Fatal("Excel dosyasının açılmış boyutu çok büyük; dosyayı küçültüp yeniden yükleyin.");
            }
        }

        stream.Position = 0;
        var rows = MiniExcel.Query(stream, useHeaderRow: false, excelType: ExcelType.XLSX).Select(ToCells);
        return GtipImportRules.Interpret(rows);
    }

    /// <summary>MiniExcel satırı sütun harfiyle (A, B, … AA) anahtarlı sözlüktür; sütun sırasına göre hücre listesine çevrilir.</summary>
    private static IReadOnlyList<object?> ToCells(object row)
    {
        if (row is not IDictionary<string, object?> cells || cells.Count == 0)
        {
            return [];
        }

        var list = new object?[cells.Keys.Max(ColumnIndex) + 1];
        foreach (var (column, value) in cells)
        {
            list[ColumnIndex(column)] = value;
        }

        return list;
    }

    /// <summary>"A" → 0, "B" → 1, "AA" → 26.</summary>
    private static int ColumnIndex(string letters)
    {
        var index = 0;
        foreach (var c in letters.ToUpperInvariant())
        {
            if (c is < 'A' or > 'Z')
            {
                throw new FormatException($"Geçersiz sütun adı: {letters}");
            }

            index = (index * 26) + (c - 'A' + 1);
        }

        return index - 1;
    }
}
