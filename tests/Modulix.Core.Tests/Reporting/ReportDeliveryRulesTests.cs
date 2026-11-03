using System.Text;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Reporting;

namespace Modulix.Core.Tests.Reporting;

public sealed class ReportDeliveryRulesTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    // ---- Takvim

    [Fact]
    public void Daily_runs_today_if_the_hour_is_still_ahead_otherwise_tomorrow_in_the_tenant_time_zone()
    {
        // Istanbul UTC+3: 09:00 yerel = 06:00 UTC.
        Assert.Equal(Utc(2026, 11, 3, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Daily, 9, null, null, Utc(2026, 11, 3, 5, 59), Istanbul));
        Assert.Equal(Utc(2026, 11, 4, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Daily, 9, null, null, Utc(2026, 11, 3, 6, 0), Istanbul));
    }

    [Fact]
    public void Local_day_not_utc_day_decides_which_day_is_today()
    {
        // 3 Kasım 22:30 UTC = 4 Kasım 01:30 İstanbul: bugün 4 Kasım; 09:00 yerel henüz gelmedi.
        Assert.Equal(Utc(2026, 11, 4, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Daily, 9, null, null, Utc(2026, 11, 3, 22, 30), Istanbul));
    }

    [Fact]
    public void Weekly_picks_the_requested_weekday()
    {
        // 3 Kasım 2026 Salı. Perşembe 10:00 İstanbul = 07:00 UTC.
        Assert.Equal(Utc(2026, 11, 5, 7), ReportScheduleRules.NextRun(ScheduleFrequency.Weekly, 10, DayOfWeek.Thursday, null, Utc(2026, 11, 3, 12), Istanbul));
        // Aynı gün ve saat geçmişse bir sonraki hafta.
        Assert.Equal(Utc(2026, 11, 10, 7), ReportScheduleRules.NextRun(ScheduleFrequency.Weekly, 10, DayOfWeek.Tuesday, null, Utc(2026, 11, 3, 12), Istanbul));
    }

    [Fact]
    public void Monthly_uses_a_day_between_1_and_28_and_rolls_over_the_year()
    {
        Assert.Equal(Utc(2026, 11, 15, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Monthly, 9, null, 15, Utc(2026, 11, 3), Istanbul));
        Assert.Equal(Utc(2027, 1, 1, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Monthly, 9, null, 1, Utc(2026, 12, 5), Istanbul));
        // Şubat'ta 28. gün var (artık gün seçilemez, böylece hiçbir ay atlanmaz).
        Assert.Equal(Utc(2027, 2, 28, 6), ReportScheduleRules.NextRun(ScheduleFrequency.Monthly, 9, null, 28, Utc(2027, 2, 1), Istanbul));
    }

    [Fact]
    public void A_skipped_hour_in_the_spring_gap_moves_to_the_next_valid_time()
    {
        // Berlin 29 Mart 2026 02:00 → 03:00: 02:30 yok. 02:00 yerel istenirse 03:00 yerel (01:00 UTC) olur.
        var at = ReportScheduleRules.At(new DateOnly(2026, 3, 29), 2, Berlin);

        Assert.Equal(Utc(2026, 3, 29, 1), at);
    }

    [Fact]
    public void A_repeated_hour_in_the_autumn_runs_on_its_first_occurrence()
    {
        // Berlin 25 Ekim 2026 03:00 → 02:00: 02:30 iki kez yaşanır; ilki yaz saatiyle (UTC+2) = 00:00 UTC.
        var at = ReportScheduleRules.At(new DateOnly(2026, 10, 25), 2, Berlin);

        Assert.Equal(Utc(2026, 10, 25, 0), at);
    }

    [Fact]
    public void After_a_long_pause_the_next_run_is_in_the_future_not_a_backlog()
    {
        var next = ReportScheduleRules.NextRun(ScheduleFrequency.Daily, 9, null, null, Utc(2026, 11, 3, 12), Istanbul);

        Assert.True(next > Utc(2026, 11, 3, 12));
        Assert.True(next <= Utc(2026, 11, 4, 12));
    }

    [Theory]
    [InlineData(ScheduleFrequency.Daily, 24, null, null)]
    [InlineData(ScheduleFrequency.Daily, -1, null, null)]
    [InlineData(ScheduleFrequency.Weekly, 9, null, null)]
    [InlineData(ScheduleFrequency.Monthly, 9, null, 29)]
    [InlineData(ScheduleFrequency.Monthly, 9, null, 0)]
    [InlineData(ScheduleFrequency.Monthly, 9, null, null)]
    [InlineData((ScheduleFrequency)9, 9, null, null)]
    public void Invalid_schedules_are_rejected(ScheduleFrequency frequency, int hour, DayOfWeek? dayOfWeek, int? dayOfMonth)
    {
        Assert.NotNull(ReportScheduleRules.Validate(frequency, hour, dayOfWeek, dayOfMonth));
    }

    [Fact]
    public void Valid_schedules_pass()
    {
        Assert.Null(ReportScheduleRules.Validate(ScheduleFrequency.Daily, 0, null, null));
        Assert.Null(ReportScheduleRules.Validate(ScheduleFrequency.Weekly, 23, DayOfWeek.Sunday, null));
        Assert.Null(ReportScheduleRules.Validate(ScheduleFrequency.Monthly, 9, null, 28));
    }

    // ---- CSV

    private static ReportResult Table(params object?[][] rows) => new(
        ReportRunStatus.Ok, false,
        [new("title", "Başlık", DatasetColumnKind.Text, ReportColumnRole.Value), new("amount", "Tutar", DatasetColumnKind.Money, ReportColumnRole.Value)],
        [.. rows.Select(r => new ReportResultRow(r, [], "TRY"))], [], rows.Length, rows.Length, false, false, 1, 50, Utc(2026, 11, 3), 1, []);

    private static string Csv(ReportResult result) => Encoding.UTF8.GetString(ReportCsv.Build(result)).TrimStart('﻿');

    [Theory]
    [InlineData("=SUM(A1:A9)")]
    [InlineData("+90 555 000 00 00")]
    [InlineData("-2+3")]
    [InlineData("@cmd|' /C calc'!A0")]
    [InlineData("\tgizli")]
    public void Formula_starts_are_escaped_in_text_cells_so_spreadsheets_do_not_evaluate_them(string text)
    {
        var line = Csv(Table([text, 1m])).Split("\r\n")[1];

        Assert.StartsWith("'" + text[..1], line.TrimStart('"'));
    }

    [Fact]
    public void Header_labels_are_escaped_too_because_admins_name_custom_fields()
    {
        var table = Table(["a", 1m]) with { Columns = [new("x", "=HYPERLINK(\"http://kotu\")", DatasetColumnKind.Text, ReportColumnRole.Value)] };

        Assert.StartsWith("\"'=HYPERLINK", Csv(table));
    }

    [Fact]
    public void Numbers_are_written_unescaped_with_invariant_culture_and_negative_numbers_stay_numeric()
    {
        var line = Csv(Table(["Ürün", -1234.5m])).Split("\r\n")[1];

        Assert.Equal("Ürün,-1234.5,TRY", line);
    }

    [Fact]
    public void Commas_quotes_and_line_breaks_are_quoted()
    {
        var csv = Csv(Table(["Ali, \"Veli\"\nTolga", 1m]));

        Assert.Contains("\"Ali, \"\"Veli\"\"\nTolga\",1,TRY", csv);
    }

    [Fact]
    public void Output_has_a_utf8_bom_crlf_line_ends_and_a_currency_column_for_money_tables()
    {
        var bytes = ReportCsv.Build(Table(["Çiğ köfte", 5m]));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        Assert.StartsWith("Başlık,Tutar,Para birimi\r\n", text);
        Assert.EndsWith("\r\n", text);
    }

    [Fact]
    public void Summary_export_includes_the_total_rows_aligned_with_the_columns()
    {
        var result = new ReportResult(
            ReportRunStatus.Ok, true,
            [new("stage", "Aşama", DatasetColumnKind.Choice, ReportColumnRole.Group), new("count", "Kayıt sayısı", DatasetColumnKind.Number, ReportColumnRole.Measure)],
            [new(["Aday", 2m], ["Lead"]), new(["Teklif", 1m], ["Proposal"])],
            [new([null, 3m], [])], 3, 3, false, false, 1, 2, Utc(2026, 11, 3), 1, []);

        var lines = Csv(result).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["Aşama,Kayıt sayısı", "Aday,2", "Teklif,1", "Toplam,3"], lines);
    }

    [Fact]
    public void File_slug_is_ascii_safe_and_never_empty()
    {
        Assert.Equal("sales-report-2026", ReportCsv.FileSlug("Sales Report 2026"));
        Assert.Equal("rapor", ReportCsv.FileSlug("   "));
        Assert.Equal("rapor", ReportCsv.FileSlug("ı"));
        var hostile = ReportCsv.FileSlug("../../etc/passwd");
        Assert.DoesNotContain('/', hostile);
        Assert.DoesNotContain('.', hostile);
        Assert.NotEmpty(hostile);
        Assert.True(ReportCsv.FileSlug(new string('a', 500)).Length <= 60);
    }
}
