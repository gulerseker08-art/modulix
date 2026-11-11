using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>
/// Rapor sonucunu Excel (.xlsx) dosyasına çevirir (ADR-0035). <see cref="ReportCsv"/> ile <b>aynı</b> <see cref="ReportResult"/>'tan, aynı sütun ve
/// satır düzeniyle üretilir (para birimi sütunu, toplam satırları). Dış paket kullanılmaz: dosya elle yazılmış en küçük OOXML paketidir (tek çalışma
/// sayfası, başlık satırı kalın, sütun genişliği sabit). Metin hücreleri <b>satır içi metin</b> (<c>t="inlineStr"</c>) olarak yazılır; Excel bunları
/// formül olarak yorumlamaz, bu yüzden CSV'deki kesme işareti kaçışı gerekmez (yazılsa metinde görünürdü). Sayılar sayı hücresidir.
/// </summary>
public static class ReportXlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int MaxCellLength = 32_000;   // Excel hücre sınırı 32.767 karakter

    public static byte[] Build(ReportResult result)
    {
        var money = !result.IsSummary && result.Rows.Any(r => r.Currency is not null);
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sheet.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        sheet.Append("<cols><col min=\"1\" max=\"64\" width=\"22\" customWidth=\"1\"/></cols>");
        sheet.Append("<sheetData>");

        var rowNumber = 1;
        var header = result.Columns.Select(c => c.Label).ToList();
        if (money)
        {
            header.Add("Para birimi");
        }

        AppendRow(sheet, rowNumber++, header.Select(h => (object?)h), bold: true);
        foreach (var row in result.Rows)
        {
            var cells = row.Cells.Select(c => c).ToList();
            if (money)
            {
                cells.Add(row.Currency);
            }

            AppendRow(sheet, rowNumber++, cells, bold: false);
        }

        foreach (var total in result.IsSummary ? result.Totals : [])
        {
            var cells = total.Cells.Select(c => c).ToList();
            if (cells.Count > 0 && result.Columns[0].Role == ReportColumnRole.Group)
            {
                cells[0] = "Toplam";
            }

            AppendRow(sheet, rowNumber++, cells, bold: true);
        }

        sheet.Append("</sheetData></worksheet>");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Rapor\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "</Relationships>");
            // Stil 0: varsayılan, stil 1: kalın.
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>"
                + "</styleSheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AppendRow(StringBuilder sheet, int rowNumber, IEnumerable<object?> cells, bool bold)
    {
        sheet.Append("<row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\">");
        var column = 0;
        foreach (var cell in cells)
        {
            var reference = ColumnName(column++) + rowNumber.ToString(CultureInfo.InvariantCulture);
            var style = bold ? " s=\"1\"" : string.Empty;
            switch (Classify(cell))
            {
                case (CellKind.Number, var number):
                    sheet.Append("<c r=\"").Append(reference).Append('"').Append(style).Append("><v>").Append(number).Append("</v></c>");
                    break;
                case (CellKind.Text, var text) when text.Length > 0:
                    sheet.Append("<c r=\"").Append(reference).Append('"').Append(style).Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                        .Append(SecurityElement.Escape(Clean(text))).Append("</t></is></c>");
                    break;
                default:
                    break;   // boş hücre yazılmaz
            }
        }

        sheet.Append("</row>");
    }

    private enum CellKind { Empty, Number, Text }

    private static (CellKind Kind, string Value) Classify(object? value) => value switch
    {
        null => (CellKind.Empty, string.Empty),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => (CellKind.Empty, string.Empty),
        JsonElement { ValueKind: JsonValueKind.Number } n => (CellKind.Number, n.GetDecimal().ToString(CultureInfo.InvariantCulture)),
        JsonElement { ValueKind: JsonValueKind.True } => (CellKind.Text, "Evet"),
        JsonElement { ValueKind: JsonValueKind.False } => (CellKind.Text, "Hayır"),
        JsonElement e => (CellKind.Text, e.ToString()),
        decimal d => (CellKind.Number, d.ToString(CultureInfo.InvariantCulture)),
        int i => (CellKind.Number, i.ToString(CultureInfo.InvariantCulture)),
        long l => (CellKind.Number, l.ToString(CultureInfo.InvariantCulture)),
        double f => (CellKind.Number, f.ToString(CultureInfo.InvariantCulture)),
        bool b => (CellKind.Text, b ? "Evet" : "Hayır"),
        _ => (CellKind.Text, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>XML'de geçersiz denetim karakterlerini atar ve hücre uzunluğunu Excel sınırına indirir.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxCellLength));
        foreach (var c in text)
        {
            if (builder.Length >= MaxCellLength)
            {
                break;
            }

            if (c is '\t' or '\n' or '\r' || (c >= ' ' && c != '￾' && c != '￿'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index; n >= 0; n = n / 26 - 1)
        {
            name = (char)('A' + (n % 26)) + name;
        }

        return name;
    }
}
