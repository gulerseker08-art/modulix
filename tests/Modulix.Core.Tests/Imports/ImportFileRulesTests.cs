using System.Text;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Imports;

namespace Modulix.Core.Tests.Imports;

/// <summary>ADR-0033 Dilim 2: dosya okuma, sütun bağlama, hücre yorumu, boru hattı hatalarının eşlenmesi ve hata raporu.</summary>
public class ImportFileRulesTests
{
    private static readonly ImportField Subject = new("subject", "Konu", Required: true);
    private static readonly ImportField Budget = new("estimatedBudget", "Tahmini bütçe", ImportFieldType.Decimal);
    private static readonly ImportField Target = new("targetPurchaseDate", "Hedef tarih", ImportFieldType.Date);
    private static readonly ImportField Priority = new("priority", "Öncelik", ImportFieldType.Choice) { Options = [new("High", "Yüksek")] };

    private static byte[] Csv(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private static (JsonObject Values, IReadOnlyList<ImportRowProblem> Problems) Row(ImportField field, string? cell) =>
        ImportRows.Build([new ImportColumn(0, field)], new ImportFileRow(2, [cell]));

    [Fact]
    public void Reads_semicolon_file_keeps_row_numbers_across_blank_lines_and_quoted_cells()
    {
        var file = ImportCsvFile.Read(Csv("Konu;Şehir\r\nBir;Ankara\r\n;\r\n\"İki; satır\";\" İzmir \"\r\n"));

        Assert.Equal(';', file.Delimiter);
        Assert.Equal(["Konu", "Şehir"], file.Headers);
        Assert.Equal([2, 4], file.Rows.Select(r => r.RowNo));
        Assert.Equal(["İki; satır", "İzmir"], file.Rows[1].Cells);
    }

    [Fact]
    public void Reading_an_error_report_restores_values_escaped_against_formula_injection()
    {
        var file = ImportCsvFile.Read(Csv("Telefon;Not\r\n'+905321112233;'Kesme ile başlayan\r\n"));

        Assert.Equal(["+905321112233", "'Kesme ile başlayan"], file.Rows[0].Cells);
    }

    [Fact]
    public void Columns_bind_by_label_or_key_regardless_of_case_and_turkish_letters()
    {
        var map = ImportColumns.Bind(["KONU", "estimatedbudget", "Fazla", "Hata"], [Subject, Budget]);

        Assert.Equal(["subject", "estimatedBudget"], map.Columns.Select(c => c.Field.Key));
        Assert.Equal(["Fazla"], map.Unknown);
        Assert.Empty(map.Errors);
    }

    [Fact]
    public void Missing_required_column_and_repeated_column_are_file_errors()
    {
        Assert.Contains("Zorunlu sütun yok: Konu.", ImportColumns.Bind(["Tahmini bütçe"], [Subject, Budget]).Errors);
        Assert.Contains("'Konu' sütunu dosyada birden çok kez var.", ImportColumns.Bind(["Konu", "subject"], [Subject]).Errors);
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234,5", 1234.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("250", 250)]
    public void Turkish_numbers_are_read(string text, double expected)
    {
        var (values, problems) = Row(Budget, text);

        Assert.Empty(problems);
        Assert.Equal((decimal)expected, values["estimatedBudget"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("1,234.56")]
    [InlineData("on bin")]
    public void Numbers_that_could_be_misread_are_rejected(string text)
    {
        var problem = Assert.Single(Row(Budget, text).Problems);

        Assert.Equal(ImportIssueCodes.FormatInvalid, problem.Code);
        Assert.DoesNotContain(text, problem.Message);
    }

    [Theory]
    [InlineData("31.12.2026", "2026-12-31")]
    [InlineData("1.2.2026", "2026-02-01")]
    [InlineData("2026-02-01", "2026-02-01")]
    public void Dates_are_normalized(string text, string expected) =>
        Assert.Equal(expected, Row(Target, text).Values["targetPurchaseDate"]!.GetValue<string>());

    [Theory]
    [InlineData("12/31/2026")]
    [InlineData("31.02.2026")]
    public void Invalid_dates_are_rejected(string text) =>
        Assert.Equal(ImportIssueCodes.FormatInvalid, Assert.Single(Row(Target, text).Problems).Code);

    [Theory]
    [InlineData("YÜKSEK")]
    [InlineData("high")]
    public void Choice_accepts_label_or_key(string text) =>
        Assert.Equal("High", Row(Priority, text).Values["priority"]!.GetValue<string>());

    [Fact]
    public void Unknown_choice_is_a_lookup_problem() =>
        Assert.Equal(ImportIssueCodes.LookupUnresolved, Assert.Single(Row(Priority, "Çok acil").Problems).Code);

    [Fact]
    public void Empty_required_cell_is_reported_and_empty_optional_cell_is_omitted()
    {
        Assert.Equal(ImportIssueCodes.RequiredMissing, Assert.Single(Row(Subject, null).Problems).Code);
        Assert.False(Row(Budget, null).Values.ContainsKey("estimatedBudget"));
    }

    [Fact]
    public void Custom_fields_go_under_the_custom_object_and_have_their_own_code()
    {
        var yesNo = new ImportField("custom.ihracat", "İhracat", ImportFieldType.YesNo);

        Assert.True(Row(yesNo, "Evet").Values["custom"]!["ihracat"]!.GetValue<bool>());
        Assert.Equal(ImportIssueCodes.CustomFieldInvalid, Assert.Single(Row(yesNo, "belki").Problems).Code);
    }

    [Fact]
    public void Multi_choice_values_are_split_by_pipe()
    {
        var field = new ImportField("custom.kanal", "Kanal", ImportFieldType.MultiChoice) { Options = [new("web", "Web"), new("fuar", "Fuar")] };

        Assert.Equal(["web", "fuar"], Row(field, "Web | Fuar").Values["custom"]!["kanal"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(ImportIssueCodes.LookupUnresolved, Assert.Single(Row(field, "Web|Radyo").Problems).Code);
    }

    [Fact]
    public void Pipeline_errors_map_to_closed_issue_codes()
    {
        var values = new JsonObject { ["email"] = "gecersiz" };
        var invalid = new RecordError(RecordErrorCode.ValidationFailed, "Doğrulama hatası.",
            [new RecordFieldError("email", "Geçerli bir e-posta adresi girin."), new RecordFieldError("lastName", "Talep sahibini tanıtan bir bilgi girin.")]);

        Assert.Equal(
            [ImportIssueCodes.FormatInvalid, ImportIssueCodes.RequiredMissing],
            ImportRows.FromRecordError(invalid, values).Select(p => p.Code));
        Assert.Equal(
            ImportIssueCodes.PermissionDenied,
            Assert.Single(ImportRows.FromRecordError(new RecordError(RecordErrorCode.PermissionDenied, "Yetki yok."), values)).Code);
        Assert.Equal(
            ImportIssueCodes.DuplicateExact,
            Assert.Single(ImportRows.FromRecordError(new RecordError(RecordErrorCode.Conflict, "Aynı anahtar."), values)).Code);
    }

    [Fact]
    public void Error_report_keeps_the_source_delimiter_adds_the_error_and_escapes_formulas()
    {
        var file = new ImportFileContent(';', ["Konu", "Not"], []);
        var row = new ImportFileRow(2, ["=HYPERLINK(1)", "a;b"]);

        var text = Encoding.UTF8.GetString(ImportErrorReport.Build(file, [(row, "Konu geçersiz.")]));

        Assert.Equal("﻿Konu;Not;Hata\r\n'=HYPERLINK(1);\"a;b\";Konu geçersiz.\r\n", text);
    }

    [Fact]
    public void Template_is_a_header_row_of_labels() =>
        Assert.Equal("﻿Konu;Tahmini bütçe\r\n", Encoding.UTF8.GetString(ImportErrorReport.Template([Subject, Budget])));
}
