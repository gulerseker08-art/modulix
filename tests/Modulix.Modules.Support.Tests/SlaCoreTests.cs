using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024 dilim 2: iş takvimi, SLA saati, politika seçimi ve eskalasyon kural doğrulaması. Saat parametreyle verilir; gerçek saat beklenmez.</summary>
public class SlaCoreTests
{
    private const string Istanbul = "Europe/Istanbul";

    private static readonly TimeOnly Nine = new(9, 0);
    private static readonly TimeOnly Six = new(18, 0);

    private static CalendarSpec Weekdays(string zone = Istanbul, params CalendarHoliday[] holidays) =>
        new(zone, false, [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }.Select(d => new DayHours(d, Nine, Six))], holidays);

    /// <summary>Yerel (İstanbul, UTC+3) saatten UTC an.</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) => new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(3)).ToUniversalTime();

    [Fact]
    public void Friday_afternoon_two_working_hours_ends_on_monday_morning()
    {
        var calendar = new WorkCalendar(Weekdays());

        // Pzt-Cum 09:00-18:00, hedef 2 iş saati: Cuma 17:00 → Cuma'da 1 saat kalır, Pazartesi 10:00.
        Assert.Equal(Local(11, 9, 10), calendar.AddWorkingTime(Local(11, 6, 17), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_holiday_on_monday_pushes_the_target_to_tuesday()
    {
        var calendar = new WorkCalendar(Weekdays(holidays: new CalendarHoliday(new DateOnly(2026, 11, 9), "Tatil", null, null)));

        Assert.Equal(Local(11, 10, 10), calendar.AddWorkingTime(Local(11, 6, 17), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_half_day_holiday_only_counts_its_own_window()
    {
        var half = new CalendarHoliday(new DateOnly(2026, 11, 9), "Arife", new TimeOnly(9, 0), new TimeOnly(13, 0));
        var calendar = new WorkCalendar(Weekdays(holidays: half));

        // Cuma 17:00'den 4 saat: Cuma'da 1 saat + Pazartesi (yarım gün 09:00-13:00) 3 saat → 12:00.
        Assert.Equal(Local(11, 9, 12), calendar.AddWorkingTime(Local(11, 6, 17), TimeSpan.FromHours(4)));
        // 4 saatten fazlası yarım günü doldurup Salı'ya taşar (1 + 4 = 5 saat → 6. saat Salı 10:00).
        Assert.Equal(Local(11, 10, 10), calendar.AddWorkingTime(Local(11, 6, 17), TimeSpan.FromHours(6)));
    }

    [Fact]
    public void Starting_outside_working_hours_waits_for_the_next_window_and_weekends_do_not_count()
    {
        var calendar = new WorkCalendar(Weekdays());

        Assert.Equal(Local(11, 9, 10), calendar.AddWorkingTime(Local(11, 7, 12), TimeSpan.FromHours(1))); // Cumartesi 12:00 → Pzt 10:00
        Assert.Equal(Local(11, 9, 10), calendar.AddWorkingTime(Local(11, 9, 7), TimeSpan.FromHours(1)));  // Pzt 07:00 → 09:00 açılır → 10:00
        Assert.Equal(Local(11, 6, 18), calendar.AddWorkingTime(Local(11, 6, 17), TimeSpan.FromHours(1))); // Pencere sonunda biter, taşmaz
        Assert.Equal(Local(11, 6, 12), calendar.AddWorkingTime(Local(11, 6, 12), TimeSpan.Zero));
    }

    [Fact]
    public void A_split_day_skips_the_gap_between_windows()
    {
        var spec = new CalendarSpec(Istanbul, false, [new DayHours(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)), new DayHours(DayOfWeek.Monday, new TimeOnly(13, 0), new TimeOnly(17, 0))], []);
        var calendar = new WorkCalendar(spec);

        Assert.Equal(Local(11, 9, 14), calendar.AddWorkingTime(Local(11, 9, 11), TimeSpan.FromHours(2))); // 11-12 (1s) + 13-14 (1s)
        Assert.Equal(TimeSpan.FromHours(7), calendar.WorkingTimeBetween(Local(11, 9, 0), Local(11, 9, 23)));
    }

    [Fact]
    public void Always_open_calendars_count_calendar_time()
    {
        var calendar = WorkCalendar.AlwaysOpen;
        var start = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);

        Assert.Equal(start.AddHours(50), calendar.AddWorkingTime(start, TimeSpan.FromHours(50)));
        Assert.Equal(TimeSpan.FromHours(50), calendar.WorkingTimeBetween(start, start.AddHours(50)));
    }

    [Fact]
    public void Working_time_between_counts_only_open_windows()
    {
        var calendar = new WorkCalendar(Weekdays());

        // Cuma 16:00 - Pazartesi 11:00: Cuma 2 saat + Pazartesi 2 saat.
        Assert.Equal(TimeSpan.FromHours(4), calendar.WorkingTimeBetween(Local(11, 6, 16), Local(11, 9, 11)));
        Assert.Equal(TimeSpan.Zero, calendar.WorkingTimeBetween(Local(11, 7, 10), Local(11, 8, 20)));
        Assert.Equal(TimeSpan.Zero, calendar.WorkingTimeBetween(Local(11, 9, 11), Local(11, 9, 10)));
    }

    [Fact]
    public void Daylight_saving_gap_and_overlap_use_real_elapsed_time()
    {
        // Berlin 29 Mart 2026: 02:00 → 03:00 atlanır; 01:00-04:00 penceresi gerçekte 2 saattir.
        var spring = new WorkCalendar(new CalendarSpec("Europe/Berlin", false, [new DayHours(DayOfWeek.Sunday, new TimeOnly(1, 0), new TimeOnly(4, 0))], []));
        var from = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(2), spring.WorkingTimeBetween(from, from.AddHours(5)));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero), spring.AddWorkingTime(from, TimeSpan.FromHours(2)));

        // Berlin 25 Ekim 2026: 03:00 → 02:00 geri alınır; 02:30 belirsizdir (ilk, yaz saati karşılığı), pencere 02:30-04:00 gerçekte 2,5 saattir.
        var autumn = new WorkCalendar(new CalendarSpec("Europe/Berlin", false, [new DayHours(DayOfWeek.Sunday, new TimeOnly(2, 30), new TimeOnly(4, 0))], []));
        var day = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(2.5), autumn.WorkingTimeBetween(day, day.AddHours(6)));
    }

    [Fact]
    public void Pausing_and_resuming_moves_the_target_by_the_paused_time_only()
    {
        var calendar = new WorkCalendar(Weekdays());
        var target = TimeSpan.FromHours(4);
        var start = Local(11, 9, 9); // Pazartesi 09:00

        var due = SlaClock.DueAt(calendar, start, target, TimeSpan.Zero);
        Assert.Equal(Local(11, 9, 13), due);

        // 11:00'de müşteri beklemesi: 2 saat harcandı. Sayaç Salı 10:00'da sürerse kalan 2 saat → Salı 12:00 (Pazartesi 11:00-18:00 duraklatmada sayılmadı).
        var worked = SlaClock.WorkedAt(calendar, TimeSpan.Zero, start, Local(11, 9, 11));
        Assert.Equal(TimeSpan.FromHours(2), worked);
        Assert.Equal(Local(11, 10, 12), SlaClock.DueAt(calendar, Local(11, 10, 10), target, worked));
    }

    [Fact]
    public void Thresholds_are_real_target_fractions_independent_of_when_a_job_runs()
    {
        var calendar = new WorkCalendar(Weekdays());
        var start = Local(11, 9, 9);
        var target = TimeSpan.FromHours(4);

        Assert.Equal(Local(11, 9, 12), SlaClock.ThresholdAt(calendar, start, target, TimeSpan.Zero, 75));
        Assert.Equal(Local(11, 9, 13), SlaClock.ThresholdAt(calendar, start, target, TimeSpan.Zero, 100));
        Assert.Equal(Local(11, 9, 15), SlaClock.ThresholdAt(calendar, start, target, TimeSpan.Zero, 150));
        // Harcanan süre eşiği zaten aştıysa eşik çalışma başlangıcıdır (hemen vadesi gelmiş).
        Assert.Equal(start, SlaClock.ThresholdAt(calendar, start, target, TimeSpan.FromHours(3.5), 75));
    }

    [Fact]
    public void Calendar_specs_are_validated_and_round_trip()
    {
        var weekly = CalendarSpecParser.WeeklyToJson(Weekdays().Weekly);
        var (spec, errors) = CalendarSpecParser.Parse(Istanbul, false, weekly, """[{"date":"2026-10-29","name":"Cumhuriyet Bayramı"},{"date":"2026-10-28","start":"09:00","end":"13:00"}]""");
        Assert.Empty(errors);
        Assert.Equal(5, spec!.Weekly.Count);
        Assert.Equal(2, spec.Holidays.Count);
        Assert.Null(spec.Holidays[0].Start);
        Assert.Equal(new TimeOnly(13, 0), spec.Holidays[1].End);

        Assert.Contains(CalendarSpecParser.Parse("Yok/Olmayan", true, null, null).Errors, e => e.Field == "timeZoneId");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, false, null, null).Errors, e => e.Field == "weeklyHours");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, false, """[{"day":1,"start":"18:00","end":"09:00"}]""", null).Errors, e => e.Field == "weeklyHours");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, false, """[{"day":1,"start":"09:00","end":"12:00"},{"day":1,"start":"11:00","end":"15:00"}]""", null).Errors, e => e.Field == "weeklyHours");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, true, null, """[{"date":"bozuk"}]""").Errors, e => e.Field == "holidays");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, true, null, """[{"date":"2026-10-29"},{"date":"2026-10-29"}]""").Errors, e => e.Field == "holidays");
        Assert.Contains(CalendarSpecParser.Parse(Istanbul, true, null, """[{"date":"2026-10-29","start":"09:00"}]""").Errors, e => e.Field == "holidays");
        Assert.Empty(CalendarSpecParser.Parse(Istanbul, true, null, null).Errors);
    }

    private static SlaPolicyCandidate Policy(string name, int precedence = 100, bool isDefault = false, bool active = true, string? type = null, Guid? category = null, string? priority = null, Guid? party = null) =>
        new(Guid.NewGuid(), name, precedence, isDefault, active, type, category, priority, party);

    [Fact]
    public void Policy_selection_prefers_the_most_specific_then_precedence_then_name_and_falls_back_to_the_default()
    {
        var category = Guid.NewGuid();
        var parentCategory = Guid.NewGuid();
        var party = Guid.NewGuid();
        var facts = new SlaTicketFacts("Complaint", [category, parentCategory], "High", party);

        var fallback = Policy("Varsayılan", isDefault: true);
        var byType = Policy("Şikâyet", type: "Complaint");
        var byTypeAndPriority = Policy("Şikâyet yüksek", type: "Complaint", priority: "High");
        var byParent = Policy("Ana kategori", category: parentCategory);
        var vip = Policy("VIP", party: party, type: "Complaint", priority: "High");
        var other = Policy("Başka tür", type: "Info");
        var inactive = Policy("Pasif", type: "Complaint", priority: "High", party: party, active: false);

        Assert.Equal(vip.Id, SlaPolicySelector.Select([fallback, byType, byTypeAndPriority, byParent, vip, other, inactive], facts)!.Id);
        Assert.Equal(byTypeAndPriority.Id, SlaPolicySelector.Select([fallback, byType, byTypeAndPriority, byParent, other], facts)!.Id);
        Assert.Equal(fallback.Id, SlaPolicySelector.Select([fallback, other], facts)!.Id);

        // Eşit özgüllükte düşük öncelik sayısı kazanır; o da eşitse ad (harf duyarsız), sonra kimlik.
        var a = Policy("B politikası", precedence: 50, type: "Complaint");
        var b = Policy("a politikası", precedence: 100, type: "Complaint");
        var c = Policy("A politikası", precedence: 100, category: category);
        Assert.Equal(a.Id, SlaPolicySelector.Select([b, a], facts)!.Id);
        var lowerId = b.Id.CompareTo(c.Id) < 0 ? b.Id : c.Id;
        Assert.Equal(lowerId, SlaPolicySelector.Select([c, b], facts)!.Id);
        Assert.Equal(lowerId, SlaPolicySelector.Select([b, c], facts)!.Id);

        // Hiçbiri uymazsa ve varsayılan yoksa talep hedefsizdir.
        Assert.Null(SlaPolicySelector.Select([other, inactive], facts));
        Assert.Null(SlaPolicySelector.Select([], facts));
    }

    [Fact]
    public void Policy_selection_is_independent_of_candidate_order()
    {
        var facts = new SlaTicketFacts("Support", [], "Normal", null);
        var policies = Enumerable.Range(0, 6).Select(i => Policy($"P{i % 3}", precedence: 100, type: "Support")).ToList();
        var expected = SlaPolicySelector.Select(policies, facts)!.Id;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected, SlaPolicySelector.Select(policies.OrderBy(_ => Guid.NewGuid()), facts)!.Id);
        }
    }

    [Fact]
    public void Escalation_rules_are_validated_and_round_trip()
    {
        var user = Guid.NewGuid();
        var queue = Guid.NewGuid();
        var rules = new[]
        {
            new EscalationRule("warn", SlaMetric.Resolution, 75, SlaEscalationAction.NotifyOwner, null, null),
            new EscalationRule("lead", SlaMetric.Resolution, 100, SlaEscalationAction.NotifyUser, user, null),
            new EscalationRule("move", SlaMetric.Resolution, 150, SlaEscalationAction.Reassign, null, queue),
        };
        var (parsed, errors) = EscalationRuleParser.Parse(EscalationRuleParser.ToJson(rules));
        Assert.Empty(errors);
        Assert.Equal(rules, parsed);

        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"X\",\"metric\":\"Resolution\",\"percent\":50,\"action\":\"NotifyOwner\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"x\",\"metric\":\"Bilinmez\",\"percent\":50,\"action\":\"NotifyOwner\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"x\",\"metric\":\"Resolution\",\"percent\":0,\"action\":\"NotifyOwner\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"x\",\"metric\":\"Resolution\",\"percent\":50,\"action\":\"NotifyUser\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"x\",\"metric\":\"Resolution\",\"percent\":50,\"action\":\"Reassign\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("[{\"id\":\"x\",\"metric\":\"Resolution\",\"percent\":50,\"action\":\"NotifyOwner\"},{\"id\":\"x\",\"metric\":\"Resolution\",\"percent\":60,\"action\":\"NotifyOwner\"}]").Errors);
        Assert.NotEmpty(EscalationRuleParser.Parse("{bozuk").Errors);
        Assert.Empty(EscalationRuleParser.Parse(null).Errors);
    }
}
