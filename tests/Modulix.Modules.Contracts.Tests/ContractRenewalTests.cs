using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 8: hatırlatma eşikleri, kaçırılan eşikler, otomatik uzatma koşulları, dönem aritmetiği ve fesih kuralları (saf).</summary>
public class ContractRenewalTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Theory]
    [InlineData("90,60,30,7", "90,60,30,7")]
    [InlineData("7, 30 ,60;90", "90,60,30,7")]
    [InlineData("30,30,7", "30,7")]
    [InlineData("0,400,-5,abc,14", "14")]
    [InlineData("", "90,60,30,7")]
    [InlineData(null, "90,60,30,7")]
    [InlineData("xyz", "90,60,30,7")]
    public void Thresholds_are_unique_descending_and_fall_back_to_the_default(string? text, string expected)
    {
        Assert.Equal(expected, string.Join(',', ContractRenewalRules.ParseThresholds(text)));
    }

    [Fact]
    public void Target_is_the_notice_deadline_then_the_end_date_and_none_for_indefinite_contracts()
    {
        Assert.Equal(D("2027-09-01"), ContractRenewalRules.Target(D("2027-09-01"), D("2027-10-31"), false));
        Assert.Equal(D("2027-10-31"), ContractRenewalRules.Target(null, D("2027-10-31"), false));
        Assert.Null(ContractRenewalRules.Target(D("2027-09-01"), D("2027-10-31"), true));
        Assert.Null(ContractRenewalRules.Target(null, null, false));
    }

    [Fact]
    public void A_reminder_fires_when_the_days_left_reach_a_threshold_and_not_before()
    {
        var thresholds = ContractRenewalRules.DefaultThresholds;
        var target = D("2027-01-01");

        Assert.Null(ContractRenewalRules.DueThresholds(D("2026-09-01"), target, thresholds, new HashSet<int>()).Fire);          // 122 gün
        Assert.Equal(90, ContractRenewalRules.DueThresholds(D("2026-10-03"), target, thresholds, new HashSet<int>()).Fire);       // 90 gün
        Assert.Equal(60, ContractRenewalRules.DueThresholds(D("2026-11-02"), target, thresholds, new HashSet<int> { 90 }).Fire);  // 60 gün
        Assert.Equal(7, ContractRenewalRules.DueThresholds(D("2026-12-25"), target, thresholds, new HashSet<int> { 90, 60, 30 }).Fire);
        Assert.Null(ContractRenewalRules.DueThresholds(D("2026-12-25"), target, thresholds, new HashSet<int> { 90, 60, 30, 7 }).Fire);
    }

    [Fact]
    public void After_an_outage_only_the_tightest_missed_threshold_fires_and_wider_ones_are_reported_as_missed()
    {
        // Kalan 20 gün: 90, 60 ve 30 eşikleri hiç işlenmedi; yalnızca 30 için aktivite açılır.
        var (fire, missed) = ContractRenewalRules.DueThresholds(D("2026-12-12"), D("2027-01-01"), ContractRenewalRules.DefaultThresholds, new HashSet<int>());

        Assert.Equal(30, fire);
        Assert.Equal([60, 90], missed.Order());
    }

    [Fact]
    public void Past_targets_produce_no_reminder_and_the_notice_check_is_exclusive()
    {
        Assert.Equal((null, Array.Empty<int>()), (ContractRenewalRules.DueThresholds(D("2027-01-02"), D("2027-01-01"), ContractRenewalRules.DefaultThresholds, new HashSet<int>()).Fire, Array.Empty<int>()));
        Assert.False(ContractRenewalRules.NoticePassed(D("2027-09-01"), D("2027-09-01")));
        Assert.True(ContractRenewalRules.NoticePassed(D("2027-09-02"), D("2027-09-01")));
        Assert.False(ContractRenewalRules.NoticePassed(D("2027-09-02"), null));
    }

    private static (bool Eligible, string? Reason) Auto(
        string mode = "Automatic", int? months = 12, bool indefinite = false, string signature = "Completed", string effective = "Active", bool tenant = true, string? notice = "2027-09-01",
        string? end = "2027-10-31", bool decided = false, string today = "2027-10-31") =>
        ContractRenewalRules.AutoRenewal(mode, months, indefinite, signature, effective, tenant, notice is null ? null : D(notice), end is null ? null : D(end), decided, D(today));

    [Fact]
    public void Automatic_renewal_needs_every_condition_and_never_happens_without_the_tenant_policy()
    {
        Assert.True(Auto().Eligible);

        Assert.False(Auto(mode: "Manual").Eligible);
        Assert.False(Auto(mode: "None").Eligible);
        Assert.False(Auto(months: null).Eligible);
        Assert.False(Auto(indefinite: true).Eligible);
        Assert.False(Auto(signature: "Pending").Eligible);
        Assert.False(Auto(effective: "AwaitingStart").Eligible);
        Assert.False(Auto(effective: "Terminated").Eligible);
        Assert.False(Auto(tenant: false).Eligible);                 // açık politika yoksa uzatma yok
        Assert.False(Auto(decided: true).Eligible);                 // dönem için karar zaten var (yenilenmeyecek/taslak/uzatıldı)
        Assert.False(Auto(today: "2027-10-30").Eligible);           // dönem bitişi gelmedi
        Assert.False(Auto(notice: "2027-11-05", today: "2027-10-31").Eligible); // bildirim süresi henüz geçmedi
        Assert.True(Auto(notice: null).Eligible);                   // bildirim süresi tanımsız
        Assert.Equal("Firma otomatik yenilemeye izin vermemiş (ayar kapalı).", Auto(tenant: false).Reason);
    }

    [Theory]
    [InlineData("2027-10-31", 12, "2027-11-01", "2028-10-31")]
    [InlineData("2028-02-29", 12, "2028-03-01", "2029-02-28")]   // artık yıl sonu → sabit 365 gün değil, takvim ayı
    [InlineData("2026-12-31", 1, "2027-01-01", "2027-01-31")]
    [InlineData("2027-03-31", 3, "2027-04-01", "2027-06-30")]
    public void The_next_term_starts_the_day_after_and_lasts_calendar_months(string currentEnd, int months, string start, string end)
    {
        var term = ContractRenewalRules.NextTerm(D(currentEnd), months);

        Assert.Equal((D(start), D(end)), (term.Start, term.End));
    }

    [Fact]
    public void Suggested_months_prefer_the_renewal_period_then_the_current_term_length()
    {
        Assert.Equal(6, ContractRenewalRules.SuggestedMonths(6, D("2026-11-01"), D("2027-10-31")));
        Assert.Equal(12, ContractRenewalRules.SuggestedMonths(null, D("2026-11-01"), D("2027-10-31")));
        Assert.Equal(3, ContractRenewalRules.SuggestedMonths(null, D("2026-11-01"), D("2027-01-31")));
        Assert.Equal(12, ContractRenewalRules.SuggestedMonths(null, null, null));
    }

    [Fact]
    public void Termination_needs_a_signed_binding_contract_and_flags_short_notice_without_blocking_immediate_dates()
    {
        var today = D("2027-03-01");

        Assert.True(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, D("2027-06-01"), today, 30).Allowed);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, D("2027-06-01"), today, 30).ShortNotice);
        Assert.True(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, D("2027-03-15"), today, 30).ShortNotice);
        Assert.True(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, today, today, 30).Immediate);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, D("2027-06-01"), today, 30).Immediate);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.Active, SignatureStatus.Completed, D("2027-03-15"), today, null).ShortNotice);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.NotInForce, SignatureStatus.NotSent, D("2027-06-01"), today, 30).Allowed);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.Terminated, SignatureStatus.Completed, D("2027-06-01"), today, 30).Allowed);
        Assert.False(ContractRenewalRules.Terminate(EffectiveStatus.Expired, SignatureStatus.Completed, D("2027-06-01"), today, 30).Allowed);
    }

    [Fact]
    public void Non_renewal_reasons_and_outcomes_are_a_closed_set()
    {
        Assert.Contains(NonRenewalReason.Price, NonRenewalReason.All);
        Assert.DoesNotContain("Bilinmeyen", NonRenewalReason.All);
        Assert.Equal(3, RenewalOutcome.All.Count);
        Assert.True(ReminderStatus.IsRetryable(ReminderStatus.Failed));
        Assert.False(ReminderStatus.IsRetryable(ReminderStatus.Created));
        Assert.False(ReminderStatus.IsRetryable(ReminderStatus.Unavailable));
    }
}
