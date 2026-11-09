using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Tests;

/// <summary>"Benim günüm" öneri ve "bu ay kapanabilecekler" kuralları (veritabanı gerektirmez). Gün farkları firmanın saat dilimindedir.</summary>
public class MyDayRulesTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    // 9.11.2026 pazartesi, İstanbul'da 10:00.
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 9);

    // Firma ayarları (teklif hatırlatma, fırsat hareketsizlik eşiği).
    private const int QuoteDays = 3;
    private const int StaleDays = 14;

    private static MyDayOpportunity Opportunity(
        string number, decimal? amount, DateOnly? close, int? probability = 50, DateTimeOffset? contact = null, DateTimeOffset? entered = null) =>
        new(Guid.NewGuid(), number, $"Fırsat {number}", "proposal", amount, "TRY", probability, close, contact, entered);

    private static MyDayLead Lead(string number, DateTimeOffset received) =>
        new(Guid.NewGuid(), number, $"Aday {number}", "Kuzey Lojistik", "web", received);

    private static MyDayQuote Quote(string number, DateTimeOffset presented) =>
        new(Guid.NewGuid(), number, $"Teklif {number}", "Ege Liman", 480_000m, "TRY", presented);

    [Fact]
    public void Suggestions_take_one_of_each_kind_in_priority_order_before_seconds_and_stop_at_five()
    {
        var opportunities = new[]
        {
            Opportunity("F-1", 100m, Today.AddDays(-3)),
            Opportunity("F-2", 900m, Today.AddDays(-10)),
            Opportunity("F-3", 50m, Today.AddDays(20), contact: Now.AddDays(-30)),
        };
        var leads = new[] { Lead("L-2", Now.AddHours(-2)), Lead("L-1", Now.AddDays(-1)) };
        var quotes = new[] { Quote("T-1", Now.AddDays(-5)) };

        var steps = MyDayRules.Suggest(opportunities, leads, quotes, Now, Istanbul, staleDays: StaleDays);

        Assert.Equal(
            ["L-1", "F-2", "T-1", "F-3", "L-2"],
            steps.Select(s => s.Number));
        Assert.Equal(
            [MyDaySuggestionKind.NewLead, MyDaySuggestionKind.OverdueOpportunity, MyDaySuggestionKind.QuoteFollowUp, MyDaySuggestionKind.StaleOpportunity, MyDaySuggestionKind.NewLead],
            steps.Select(s => s.Kind));
        Assert.Equal(10, steps[1].DaysOverdue);
        Assert.Equal(5, steps[2].DaysWaiting);
        Assert.Equal(30, steps[3].DaysSinceContact);
    }

    [Fact]
    public void Fresh_quotes_recent_contacts_and_undated_opportunities_are_not_suggested()
    {
        var opportunities = new[]
        {
            Opportunity("F-1", 100m, Today.AddDays(10), contact: Now.AddDays(-(StaleDays - 1))),
            Opportunity("F-2", 100m, null),
            Opportunity("F-3", 100m, Today, contact: Now.AddDays(-40)),
        };
        var quotes = new[] { Quote("T-1", Now.AddDays(-(QuoteDays - 1))) };

        var steps = MyDayRules.Suggest(opportunities, [], quotes, Now, Istanbul, quoteReminderDays: QuoteDays, staleDays: StaleDays);

        // Bugün kapanacak fırsat gecikmiş değildir; temas 40 gün önce olduğu için temassız sayılır.
        var step = Assert.Single(steps);
        Assert.Equal(("F-3", MyDaySuggestionKind.StaleOpportunity), (step.Number, step.Kind));
    }

    [Fact]
    public void An_overdue_opportunity_is_suggested_once_and_stage_entry_stands_in_for_missing_contact()
    {
        var opportunities = new[]
        {
            Opportunity("F-1", 100m, Today.AddDays(-1), contact: Now.AddDays(-60)),
            Opportunity("F-2", 100m, Today.AddDays(5), entered: Now.AddDays(-StaleDays)),
        };

        var steps = MyDayRules.Suggest(opportunities, [], [], Now, Istanbul, staleDays: StaleDays);

        Assert.Equal([("F-1", MyDaySuggestionKind.OverdueOpportunity), ("F-2", MyDaySuggestionKind.StaleOpportunity)], steps.Select(s => (s.Number, s.Kind)));
        Assert.Equal(60, steps[0].DaysSinceContact);
        Assert.Equal(StaleDays, steps[1].DaysSinceContact);
    }

    [Fact]
    public void An_accepted_quote_on_an_open_opportunity_comes_first_and_the_opportunity_is_not_suggested_again()
    {
        var overdue = Opportunity("F-1", 100m, Today.AddDays(-4));
        var other = Opportunity("F-2", 900m, Today.AddDays(-2));
        var closedId = Guid.NewGuid();
        var accepted = new[]
        {
            new MyDayAcceptedQuote(Guid.NewGuid(), "T-1", overdue.Id, 120m, "TRY", Now.AddDays(-6)),
            new MyDayAcceptedQuote(Guid.NewGuid(), "T-2", overdue.Id, 150m, "TRY", Now.AddDays(-2)),
            // Fırsatı açık listede olmayan (kapanmış ya da başkasının) teklif önerilmez.
            new MyDayAcceptedQuote(Guid.NewGuid(), "T-3", closedId, 999m, "TRY", Now.AddDays(-1)),
        };

        var steps = MyDayRules.Suggest([overdue, other], [], [], Now, Istanbul, accepted, QuoteDays, StaleDays);

        Assert.Equal(
            [(MyDaySuggestionKind.AcceptedQuote, "F-1"), (MyDaySuggestionKind.OverdueOpportunity, "F-2")],
            steps.Select(s => (s.Kind, s.Number)));
        // Aynı fırsatın en son kabul edilen teklifi gösterilir; tutar teklifin net toplamıdır.
        Assert.Equal(("T-2", 150m, 2), (steps[0].Subtitle, steps[0].Amount!.Value, steps[0].DaysWaiting!.Value));
    }

    [Fact]
    public void Zero_thresholds_turn_off_quote_reminders_and_stale_suggestions()
    {
        var steps = MyDayRules.Suggest(
            [Opportunity("F-1", 100m, Today.AddDays(10), contact: Now.AddDays(-400))], [], [Quote("T-1", Now.AddDays(-100))], Now, Istanbul,
            quoteReminderDays: 0, staleDays: 0);

        Assert.Empty(steps);
    }

    [Fact]
    public void Closing_this_month_keeps_the_calendar_month_orders_by_probability_then_amount_and_flags_overdue()
    {
        var opportunities = new[]
        {
            Opportunity("F-1", 100m, new DateOnly(2026, 11, 2), probability: 65),
            Opportunity("F-2", 900m, new DateOnly(2026, 11, 24), probability: 80),
            Opportunity("F-3", 500m, new DateOnly(2026, 11, 28), probability: 65),
            Opportunity("F-4", 999m, new DateOnly(2026, 12, 1), probability: 90),
            Opportunity("F-5", 999m, new DateOnly(2026, 10, 31), probability: 90),
            Opportunity("F-6", 10m, new DateOnly(2026, 11, 30), probability: null),
        };

        var closing = MyDayRules.ClosingThisMonth(opportunities, Today);

        Assert.Equal(["F-2", "F-3", "F-1", "F-6"], closing.Select(c => c.Number));
        Assert.Equal([false, false, true, false], closing.Select(c => c.Overdue));
    }

    [Theory]
    [InlineData("2026-11-09", "2026-11-09")]
    [InlineData("2026-11-15", "2026-11-09")]
    [InlineData("2026-11-11", "2026-11-09")]
    public void Week_starts_on_monday(string day, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), MyDayRules.WeekStart(DateOnly.Parse(day)));
}
