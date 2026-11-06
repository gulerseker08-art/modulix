using System.Text;
using Modulix.Core.Customs;

namespace Modulix.Core.Tests.Customs;

/// <summary>GTİP dosyası yorumlama kuralları (ADR-0032 K9): başlık eşleme, hücre normalleştirme, satır hataları, CSV çözümleme.</summary>
public class GtipImportRulesTests
{
    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    [Theory]
    [InlineData("GTİP", "Tanım", "Birim")]
    [InlineData("gtip kodu", "Eşya Tanımı", "Ölçü Birimi")]
    [InlineData("Kod", "Açıklama", "Ek birim")]
    [InlineData("HS Code", "Description", "Unit")]
    public void Header_names_are_matched_ignoring_case_and_turkish_letters(string code, string description, string unit)
    {
        var result = GtipImportRules.Interpret([Row(code, description, unit), Row("09023000", "Siyah çay", "kg")]);

        Assert.Null(result.FatalError);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(new GtipCatalogEntryInput("09023000", "Siyah çay", "kg"), entry);
    }

    [Fact]
    public void Header_may_follow_title_rows_and_columns_may_be_in_any_order()
    {
        var result = GtipImportRules.Interpret(
        [
            Row("2026 Gümrük Tarife Cetveli"),
            Row(),
            Row("Tanım", null, "GTİP"),
            Row("Kakao tozu", null, "1805.00.00"),
        ]);

        Assert.Null(result.FatalError);
        Assert.Equal([new GtipCatalogEntryInput("18050000", "Kakao tozu", null)], result.Entries);
    }

    [Fact]
    public void Missing_header_is_a_fatal_error_that_names_the_expected_columns()
    {
        var result = GtipImportRules.Interpret([Row("A", "B"), Row("09023000", "Siyah çay")]);

        Assert.NotNull(result.FatalError);
        Assert.Contains("GTİP", result.FatalError);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void Numeric_code_that_lost_its_leading_zero_is_restored_but_text_codes_are_left_alone()
    {
        var result = GtipImportRules.Interpret(
        [
            Row("GTİP", "Tanım"),
            Row(9023000, "Sayı olarak kayıtlı (8 hane)"),
            Row(90230011.0, "Ondalıksız çift sayı (8 hane, sıfır kaybı yok)"),
            Row("09023000", "Metin"),
            Row(902300011100L, "Sayı, 12 hane, sıfır kaybı yok"),
        ]);

        Assert.Equal(1, result.PaddedCodes);
        Assert.Equal(["09023000", "902300011100", "90230011"], result.Entries.Select(e => e.Code).Order(StringComparer.Ordinal));

        // Metin "09023000" satırı, sıfırı tamamlanan sayı satırıyla aynı koddur: yinelenen olarak hata sayılır.
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("09023000", result.Errors.Single().Message);
    }

    [Fact]
    public void Chapter_and_heading_rows_are_skipped_without_error()
    {
        var result = GtipImportRules.Interpret(
        [
            Row("GTİP", "Tanım"),
            Row("09", "Kahve, çay, mate ve baharat"),
            Row("0902", "Çay"),
            Row("0902.30", "Siyah çay"),
            Row("0902.30.00", "Siyah çay (fermente)"),
        ]);

        Assert.Equal(2, result.HeadingRows);
        Assert.Equal(["090230", "09023000"], result.Entries.Select(e => e.Code));
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void Bad_rows_are_reported_with_their_row_number_and_block_the_import()
    {
        var result = GtipImportRules.Interpret(
        [
            Row("GTİP", "Tanım", "Birim"),
            Row("09023000", "Siyah çay", "kg"),
            Row("ABC12345", "Harfli kod", null),
            Row(null, "Kodsuz satır", null),
            Row("18050000", null, null),
            Row("09023000", "Yinelenen kod", null),
            Row(new DateTime(2026, 5, 18), "Tarih olarak yorumlanmış", null),
            Row("85171300", "Telefon", new string('k', 40)),
        ]);

        Assert.Single(result.Entries);
        Assert.Equal(6, result.ErrorCount);
        Assert.Equal([3, 4, 5, 6, 7, 8], result.Errors.Select(e => e.Row));
        Assert.Contains("tarih", result.Errors[4].Message);
        Assert.Contains("Yinelenen", result.Errors[3].Message);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void Blank_rows_are_ignored_and_reported_errors_are_capped()
    {
        var rows = new List<IReadOnlyList<object?>> { Row("GTİP", "Tanım"), Row(), Row(null, null) };
        rows.AddRange(Enumerable.Range(0, GtipImportRules.MaxReportedErrors + 25).Select(i => Row("harf" + i, "Hatalı")));

        var result = GtipImportRules.Interpret(rows);

        Assert.Equal(GtipImportRules.MaxReportedErrors + 25, result.ErrorCount);
        Assert.Equal(GtipImportRules.MaxReportedErrors, result.Errors.Count);
        Assert.Equal(GtipImportRules.MaxReportedErrors + 25, result.RowsRead);
    }

    [Fact]
    public void File_without_any_row_is_not_importable()
    {
        Assert.False(GtipImportRules.Interpret([Row("GTİP", "Tanım")]).CanImport);
        Assert.NotNull(GtipImportRules.Interpret([]).FatalError);
    }

    [Fact]
    public void Utf8_csv_with_bom_and_semicolons_is_read_with_quoted_fields()
    {
        var text = "GTİP;Tanım;Birim\r\n0902.30.00;\"Siyah çay; fermente \"\"özel\"\"\";kg\r\n18050000;Kakao tozu;\r\n";
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var rows = GtipCsv.Parse(bytes).ToList();
        var result = GtipImportRules.Interpret(rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            [new GtipCatalogEntryInput("09023000", "Siyah çay; fermente \"özel\"", "kg"), new GtipCatalogEntryInput("18050000", "Kakao tozu", null)],
            result.Entries);
    }

    [Fact]
    public void Windows_1254_csv_from_turkish_excel_is_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1254).GetBytes("GTİP;Eşya Tanımı\n09023000;Çay, şeker ve ığdır\n");

        var result = GtipImportRules.Interpret(GtipCsv.Parse(bytes));

        Assert.Equal("Çay, şeker ve ığdır", Assert.Single(result.Entries).Description);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a\tb\tc", '\t')]
    [InlineData("tek sütun", ';')]
    public void Delimiter_is_detected_from_the_first_line(string text, char expected) =>
        Assert.Equal(expected, GtipCsv.DetectDelimiter(text));

    [Fact]
    public void Csv_without_trailing_newline_keeps_the_last_row()
    {
        var rows = GtipCsv.Parse(Encoding.UTF8.GetBytes("GTİP,Tanım\n09023000,Çay")).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Çay", rows[1][1]);
    }
}
