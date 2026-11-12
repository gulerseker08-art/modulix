using System.IO.Compression;
using MiniExcelLibs;
using Modulix.Core.Imports;

namespace Modulix.Infrastructure.Imports;

/// <summary>
/// İçe aktarma çalışma kitabı okuyucusu (ADR-0033, Dilim 4a): yalnızca <c>.xlsx</c>, yalnızca ilk sayfa, MiniExcel ile satır satır. Formül hücresinde
/// kayıtlı sonuç okunur; makro ve dış bağlantı çalıştırılmaz. Eski ikili <c>.xls</c> desteklenmez. Yorumlama <see cref="ImportFiles"/> ve
/// <see cref="ImportRows"/>'tadır; burası yalnızca biçimi hücre listesine indirir.
/// </summary>
internal sealed class ImportWorkbookReader : IImportWorkbookReader
{
    /// <summary>Açılmış (sıkıştırması çözülmüş) toplam boyut sınırı: küçük bir .xlsx'in belleği doldurmasını (zip bombası) keser.</summary>
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;

    /// <summary>Boş ama biçimlendirilmiş milyonlarca satırı olan sayfalar da vardır; bu kadar fiziksel satırdan sonra okuma bırakılır.</summary>
    private const int MaxScannedRows = 100_000;

    /// <summary>Hücre sütun sayısı üst sınırdan çok genişse (Excel 16.384 sütun açabilir) fazlası okunmaz; sütun sayısı sınırı dosya denetimindedir.</summary>
    private const int MaxScannedColumns = ImportLimits.MaxColumns + 1;

    public ImportFileContent Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // .xlsx bir zip dosyasıdır ("PK" ile başlar); metin dosyasının uzantısı değiştirilmişse anlaşılır bir ileti verilir.
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K')
        {
            throw new ImportFileException("Dosya geçerli bir .xlsx değil (Excel çalışma kitabı olarak kaydedilmemiş).");
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
                {
                    throw new ImportFileException("Excel dosyasının açılmış boyutu çok büyük; dosyayı küçültüp yeniden yükleyin.");
                }
            }

            stream.Position = 0;
            var rows = MiniExcel.Query(stream, useHeaderRow: false, excelType: ExcelType.XLSX).Select(ToCells).Take(MaxScannedRows + 1);
            var file = ImportFiles.Build(';', rows);
            return file;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or OverflowException
            or System.Xml.XmlException or IOException or KeyNotFoundException)
        {
            throw new ImportFileException("Dosya okunamadı; geçerli bir Excel (.xlsx) ya da CSV dosyası olduğundan emin olun.");
        }
    }

    /// <summary>MiniExcel satırı sütun harfiyle (A, B, … AA) anahtarlı sözlüktür; sütun sırasına göre hücre listesine çevrilir.</summary>
    private static IReadOnlyList<object?> ToCells(object row)
    {
        if (row is not IDictionary<string, object?> cells || cells.Count == 0)
        {
            return [];
        }

        var list = new object?[Math.Min(cells.Keys.Max(ColumnIndex) + 1, MaxScannedColumns)];
        foreach (var (column, value) in cells)
        {
            var index = ColumnIndex(column);
            if (index < list.Length)
            {
                list[index] = value;
            }
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
