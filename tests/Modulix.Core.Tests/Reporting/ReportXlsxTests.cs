using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Reporting;

namespace Modulix.Core.Tests.Reporting;

public sealed class ReportXlsxTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static ReportResult Table(string label, params object?[][] rows) => new(
        ReportRunStatus.Ok, false,
        [new("title", label, DatasetColumnKind.Text, ReportColumnRole.Value), new("amount", "Tutar", DatasetColumnKind.Money, ReportColumnRole.Value)],
        [.. rows.Select(r => new ReportResultRow(r, [], "TRY"))], [], rows.Length, rows.Length, false, false, 1, 50, new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), 1, []);

    private static XDocument Sheet(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void The_file_is_a_valid_package_with_the_expected_parts()
    {
        var file = ReportXlsx.Build(Table("Başlık", ["A", 10m]));

        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Superset(new HashSet<string> { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" }, names);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            XDocument.Load(stream);   // her parça iyi biçimli XML'dir
        }
    }

    [Fact]
    public void Header_text_numbers_and_the_currency_column_are_written_in_the_same_layout_as_csv()
    {
        var rows = Sheet(ReportXlsx.Build(Table("Başlık", ["Firma A", 1250.5m], ["Firma B", null]))).Descendants(Ns + "row").ToList();

        Assert.Equal(3, rows.Count);
        var header = rows[0].Elements(Ns + "c").Select(c => c.Value).ToList();
        Assert.Equal(["Başlık", "Tutar", "Para birimi"], header);
        var first = rows[1].Elements(Ns + "c").ToList();
        Assert.Equal("Firma A", first[0].Value);
        Assert.Null(first[1].Attribute("t"));                       // sayı hücresi
        Assert.Equal("1250.5", first[1].Element(Ns + "v")!.Value);   // değişmez kültür, nokta ondalık
        Assert.Equal("TRY", first[2].Value);
        Assert.DoesNotContain(rows[2].Elements(Ns + "c"), c => c.Attribute("r")!.Value == "B3");   // boş tutar hücre yazmaz
    }

    [Fact]
    public void Formula_like_text_is_stored_as_inline_text_never_as_a_formula()
    {
        var sheet = Sheet(ReportXlsx.Build(Table("=HYPERLINK(\"http://kotu\")", ["=SUM(A1:A9)", 1m], ["@cmd", 2m])));

        Assert.Empty(sheet.Descendants(Ns + "f"));   // formül öğesi hiç yok
        var texts = sheet.Descendants(Ns + "c").Where(c => (string?)c.Attribute("t") == "inlineStr").Select(c => c.Value).ToList();
        Assert.Contains("=SUM(A1:A9)", texts);
        Assert.Contains("@cmd", texts);
        Assert.Contains("=HYPERLINK(\"http://kotu\")", texts);
    }

    [Fact]
    public void Xml_special_and_control_characters_do_not_break_the_file()
    {
        var file = ReportXlsx.Build(Table("T", ["<a & b> \u0001\u0008 ğüşiöç", 1m]));

        var text = Sheet(file).Descendants(Ns + "c").Select(c => c.Value).First(v => v.Contains("ğüşiöç"));
        Assert.Equal("<a & b>  ğüşiöç", text);
    }

    [Fact]
    public void Column_names_continue_past_z()
    {
        var wide = new ReportResult(
            ReportRunStatus.Ok, false,
            [.. Enumerable.Range(0, 28).Select(i => new ReportResultColumn($"c{i}", $"S{i}", DatasetColumnKind.Text, ReportColumnRole.Value))],
            [new ReportResultRow([.. Enumerable.Range(0, 28).Select(i => (object?)$"v{i}")], [])], [], 1, 1, false, false, 1, 50,
            new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), 1, []);

        var refs = Sheet(ReportXlsx.Build(wide)).Descendants(Ns + "row").First().Elements(Ns + "c").Select(c => c.Attribute("r")!.Value).ToList();

        Assert.Equal(["A1", "Z1", "AA1", "AB1"], new[] { refs[0], refs[25], refs[26], refs[27] });
    }
}
