using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 1: satış genel görünümü (metrikler, kur, hedef), hedefler (sürüm, çakışma) ve sürümlü tahmin (kategori, düzeltme, kilit, yeniden açma,
/// karşılaştırma). Her özellik için mutlu yol, doğrulama, tenant izolasyonu ve yetki testleri.
/// </summary>
public class SalesPlanningTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] ManagerPermissions =
    [
        CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead, OpportunitiesChangeStage, OpportunitiesClose,
        SalesReportsRead, SalesTargetsRead, SalesTargetsManage, ForecastsRead, ForecastsWrite, ForecastsSubmit, ForecastsLock, ForecastsReopen, PricesRead, PricesWrite,
    ];

    // ---- Genel görünüm: metrikler (kabul senaryoları 1, 2)

    [Fact]
    public async Task Overview_counts_period_outlook_without_double_counting_won_or_next_month()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "C kazanıldı", 150_000m);
        await OpenAsync(s.Admin, s.CompanyId, "A", 100_000m, 40, ThisMonth(anchor));
        await OpenAsync(s.Admin, s.CompanyId, "B", 200_000m, 70, ThisMonth(anchor));
        await OpenAsync(s.Admin, s.CompanyId, "D gelecek ay", 300_000m, 50, anchor.AddMonths(1));

        var overview = await OverviewAsync(s.Admin, anchor);

        Assert.True(overview.AmountsVisible);
        Assert.Equal(300_000m, overview.ExpectedOpen.Amount);
        Assert.Equal(180_000m, overview.WeightedOpen.Amount);
        Assert.Equal(150_000m, overview.Realized.Amount);
        Assert.Equal(330_000m, overview.Outlook);
        Assert.Equal(600_000m, overview.OpenPipeline.Amount);
        Assert.Equal(1, overview.WonCount);
        Assert.NotEmpty(overview.Definitions);
        Assert.False(overview.Realized.IsPartial);
    }

    [Fact]
    public async Task Overview_compares_with_the_same_point_of_the_previous_period_and_reports_days_in_stage()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Bu ay kazanıldı", 150_000m);
        await OpenAsync(s.Admin, s.CompanyId, "Açık", 100_000m, 40, ThisMonth(anchor));
        var monthStart = new DateOnly(anchor.Year, anchor.Month, 1);
        var previousStart = monthStart.AddMonths(-1);

        var overview = await OverviewAsync(s.Admin, anchor);

        var previous = overview.PreviousToDate!;
        Assert.Equal(previousStart, previous.Start);
        var sameDay = previousStart.AddDays(anchor.Day - 1);
        Assert.Equal(sameDay < monthStart ? sameDay : monthStart.AddDays(-1), previous.LastDay);
        Assert.Equal(0m, previous.Realized.Amount);
        Assert.Equal(0, previous.WonCount);
        Assert.Null(previous.WinRatePercent);
        Assert.Equal(0m, Assert.Single(overview.ByStage).AverageDaysInStage);

        var finished = (await OverviewAsync(s.Admin, previousStart)).PreviousToDate!;
        Assert.Equal(previousStart.AddMonths(-1), finished.Start);
        Assert.Equal(previousStart.AddDays(-1), finished.LastDay);
        Assert.Null((await OverviewAsync(s.Admin, monthStart.AddMonths(1))).PreviousToDate);
    }

    [Fact]
    public async Task Overview_reports_target_progress_remaining_and_coverage()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanıldı", 150_000m);
        await OpenAsync(s.Admin, s.CompanyId, "A", 100_000m, 40, ThisMonth(anchor));
        await OpenAsync(s.Admin, s.CompanyId, "B", 200_000m, 70, ThisMonth(anchor));

        var (start, end) = MonthRange(anchor);
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 500_000m));
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonCount", "Tenant", null, null, start, end, 4m));

        var overview = await OverviewAsync(s.Admin, anchor);

        var amount = overview.AmountTarget!;
        Assert.Equal("Tenant", amount.Source);
        Assert.Equal(30m, amount.Progress!.AttainmentPercent);
        Assert.Equal(350_000m, amount.Progress.Remaining);
        Assert.Equal(0.857m, amount.Progress.Coverage);
        Assert.Equal(25m, overview.CountTarget!.Progress!.AttainmentPercent);
    }

    [Fact]
    public async Task Overview_without_a_target_has_no_ratios_and_an_exceeded_target_is_met()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanıldı", 150_000m);
        Assert.Null((await OverviewAsync(s.Admin, anchor)).AmountTarget!.Progress);

        var (start, end) = MonthRange(anchor);
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 100_000m));
        var progress = (await OverviewAsync(s.Admin, anchor)).AmountTarget!.Progress!;

        Assert.True(progress.TargetMet);
        Assert.Null(progress.Coverage);
        Assert.Equal(150m, progress.AttainmentPercent);
    }

    [Fact]
    public async Task Card_detail_total_matches_the_card_and_flags_missing_rates_instead_of_assuming_one()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanıldı", 1_000m);
        await OpenAsync(s.Admin, s.CompanyId, "TRY", 100m, 50, ThisMonth(anchor));
        await OpenAsync(s.Admin, s.CompanyId, "USD kursuz", 10m, 50, ThisMonth(anchor), currency: "USD");

        var overview = await OverviewAsync(s.Admin, anchor);
        var detail = await DetailAsync(s.Admin, "ExpectedOpen", anchor);

        Assert.Equal(100m, overview.ExpectedOpen.Amount);
        Assert.Equal(1, overview.ExpectedOpen.MissingRateCount);
        Assert.True(overview.ExpectedOpen.IsPartial);
        Assert.Contains(overview.Warnings, w => w.Contains("kuru bulunamadı"));
        Assert.Equal((decimal?)overview.ExpectedOpen.Amount, detail.Rows.Where(r => r.Issue is null).Sum(r => r.Amount));
        Assert.Equal(overview.ExpectedOpen.Count, detail.RowCount);
        Assert.Single(detail.Rows, r => r.Issue == "MissingRate");

        // Kur tanımlanınca aynı fırsat dönüştürülüp toplama girer.
        await PostAsync<ExchangeRateDto>(s.Admin, "/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 30m, anchor.AddDays(-5)));
        var after = await OverviewAsync(s.Admin, anchor);
        Assert.Equal(400m, after.ExpectedOpen.Amount);
        Assert.Equal(0, after.ExpectedOpen.MissingRateCount);
    }

    // ---- Genel görünüm: yetki ve izolasyon

    [Fact]
    public async Task Overview_needs_its_permission_hides_amounts_without_the_amount_permission_and_follows_data_scope()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Yöneticinin", 100m);
        await OpenAsync(s.Admin, s.CompanyId, "Yönetici fırsatı", 7_654.32m, 50, ThisMonth(anchor));

        // Rapor izni yok → 403.
        var plain = await UserAsync(s, [CompaniesRead, OpportunitiesRead], Scope.Own);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/crm/sales/overview")).StatusCode);

        // Tutar izni yok → tutarlar toplanmaz, adetler görünür.
        var noAmounts = await UserAsync(s, [CompaniesRead, OpportunitiesRead, SalesReportsRead], Scope.All);
        var hidden = await OverviewAsync(noAmounts, anchor);
        Assert.False(hidden.AmountsVisible);
        Assert.Equal(0m, hidden.OpenPipeline.Amount);
        Assert.Equal(0m, hidden.Realized.Amount);
        Assert.Equal(0m, hidden.Outlook);
        Assert.Equal(1, hidden.OpenPipeline.Count);
        Assert.Equal(1, hidden.WonCount);
        Assert.DoesNotContain("7654.32", await noAmounts.GetStringAsync($"/api/crm/sales/overview?date={anchor:yyyy-MM-dd}"));

        // Veri kapsamı Kendi → yöneticinin fırsatları hiçbir toplama girmez.
        var rep = await UserAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead, SalesReportsRead], Scope.Own);
        var own = await OverviewAsync(rep, anchor);
        Assert.True(own.ScopeLimited);
        Assert.Equal(0, own.OpenPipeline.Count);
        Assert.Equal(0m, own.Realized.Amount);
        Assert.Equal(0, (await DetailAsync(rep, "OpenPipeline", anchor)).RowCount);
    }

    [Fact]
    public async Task Another_tenant_sees_nothing_of_overview_targets_and_forecasts()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        var anchor = await WinAsync(a.Admin, a.CompanyId, "A kazandı", 5_000m);
        await OpenAsync(a.Admin, a.CompanyId, "A açık", 7_000m, 60, ThisMonth(anchor));
        var (start, end) = MonthRange(anchor);
        var target = await CreateTargetAsync(a.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 9_000m));
        var forecast = await CreateForecastAsync(a.Admin, anchor);

        var other = await OverviewAsync(b.Admin, anchor);
        Assert.Equal((0, 0m, 0m), (other.OpenPipeline.Count, other.OpenPipeline.Amount, other.Realized.Amount));
        Assert.Null(other.AmountTarget!.Progress);

        // Aynı kimliklerle başka tenant'tan erişim: bulunamadı.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/crm/sales/forecasts/{forecast.Header.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/crm/sales-targets/{target.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{forecast.Header.Id}/lock", new ForecastVersionRequestDto())).StatusCode);
        Assert.Empty((await b.Admin.GetFromJsonAsync<List<ForecastHeaderDto>>("/api/crm/sales/forecasts"))!);
        Assert.Empty((await b.Admin.GetFromJsonAsync<CrmPage<SalesTargetDto>>("/api/crm/sales-targets"))!.Items);
    }

    // ---- Hedefler

    [Fact]
    public async Task Targets_reject_overlap_identity_changes_and_version_value_changes_with_a_reason()
    {
        var s = await SetUpAsync();
        var start = new DateOnly(2031, 1, 1);
        var end = new DateOnly(2031, 3, 31);
        var target = await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_000m));

        // Çakışan dönem (aynı kapsam/metrik/para birimi) reddedilir; farklı para birimi ya da metrik çakışmaz.
        var overlap = await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", end, end.AddMonths(1), 500m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overlap.StatusCode);
        Assert.Contains("periodStart", await overlap.Content.ReadAsStringAsync());
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "USD", start, end, 10m));
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonCount", "Tenant", null, null, start, end, 5m));

        // Doğrulama: adet hedefinde para birimi olmaz, tutar hedefinde zorunlu; değer pozitif; kullanıcı hedefinde kullanıcı gerekir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonCount", "Tenant", null, "TRY", start.AddYears(1), end.AddYears(1), 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonAmount", "Tenant", null, null, start.AddYears(1), end.AddYears(1), 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start.AddYears(1), end.AddYears(1), 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonAmount", "User", null, "TRY", start.AddYears(1), end.AddYears(1), 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonCount", "Tenant", null, null, start.AddYears(1), end.AddYears(1), 2.5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync("/api/crm/sales-targets", new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", end, start, 5m))).StatusCode);

        // Kimlik alanları sonradan değişmez.
        var moved = await s.Admin.PutAsJsonAsync($"/api/crm/sales-targets/{target.Id}",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end.AddDays(5), 1_000m, Version: target.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);

        // Değer değişimi gerekçe ister, sürüm numarasını artırır ve eski/yeni değeri korur.
        var noReason = await s.Admin.PutAsJsonAsync($"/api/crm/sales-targets/{target.Id}",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_500m, Version: target.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Contains("changeReason", await noReason.Content.ReadAsStringAsync());

        var updated = await ReadAsync<SalesTargetDto>(await s.Admin.PutAsJsonAsync($"/api/crm/sales-targets/{target.Id}",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_500m, ChangeReason: "Kampanya geldi", Version: target.Version)));
        Assert.Equal((2, 1_500m), (updated.RevisionNo, updated.Value));
        var revisions = await factory.ScalarAsync<int>(s.TenantId, "SELECT COUNT(*) FROM [crm].[SalesTargetRevisions] WHERE [TargetId] = @id AND [OldValue] = 1000 AND [NewValue] = 1500",
            ("@id", target.Id));
        Assert.Equal(1, revisions);

        // Not değişimi sürüm açmaz.
        var note = await ReadAsync<SalesTargetDto>(await s.Admin.PutAsJsonAsync($"/api/crm/sales-targets/{target.Id}",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_500m, Note: "Not", Version: updated.Version)));
        Assert.Equal(2, note.RevisionNo);
    }

    [Fact]
    public async Task Target_management_needs_its_own_permissions()
    {
        var s = await SetUpAsync();
        var readOnly = await UserAsync(s, [SalesTargetsRead], Scope.All);
        var none = await UserAsync(s, [CompaniesRead], Scope.All);
        var request = new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", new DateOnly(2032, 1, 1), new DateOnly(2032, 1, 31), 10m);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/crm/sales-targets", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/crm/sales-targets")).StatusCode);
        var created = await CreateTargetAsync(s.Admin, request);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/api/crm/sales-targets/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/crm/sales-targets/{created.Id}?version={created.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/crm/sales-targets/{created.Id}?version={created.Version}")).StatusCode);
    }

    [Fact]
    public async Task User_targets_roll_up_for_a_scoped_user_and_child_excess_over_the_tenant_target_is_warned_not_added()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanıldı", 100m);
        var (start, end) = MonthRange(anchor);
        var rep = await UserWithIdAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead, SalesReportsRead, SalesTargetsRead], Scope.Own);

        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_000m));
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "User", rep.UserId, "TRY", start, end, 700m));
        await CreateTargetAsync(s.Admin, new SaveSalesTargetRequest("WonAmount", "User", s.AdminUser.Id, "TRY", start, end, 600m));

        // Yönetici (tüm kapsam): tenant hedefi esastır, alt hedefler eklenmez (1.000, 1.300 değil) ve aşım uyarılır.
        var overview = await OverviewAsync(s.Admin, anchor);
        Assert.Equal(("Tenant", 1_000m), (overview.AmountTarget!.Source, overview.AmountTarget.Value));
        Assert.Equal(300m, overview.AmountTarget.ChildExcess);
        Assert.Contains(overview.Warnings, w => w.Contains("tenant hedefini"));

        // Kapsamı kısıtlı temsilci kendi hedefini görür (tenant hedefiyle karşılaştırılmaz).
        var own = await OverviewAsync(rep.Client, anchor);
        Assert.Equal(("UserSum", 700m), (own.AmountTarget!.Source, own.AmountTarget.Value));
    }

    // ---- Tahmin

    [Fact]
    public async Task Forecast_lifecycle_snapshot_adjustments_scenarios_lock_and_reopen_as_a_new_version()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanılan", 150_000m);
        var a = await OpenAsync(s.Admin, s.CompanyId, "A", 100_000m, 40, ThisMonth(anchor));
        var b = await OpenAsync(s.Admin, s.CompanyId, "B", 200_000m, 70, ThisMonth(anchor));
        var hot = await OpenAsync(s.Admin, s.CompanyId, "Sıcak", 50_000m, 90, ThisMonth(anchor));

        var draft = await CreateForecastAsync(s.Admin, anchor);
        Assert.Equal((1, "Draft"), (draft.Header.VersionNo, draft.Header.Status));
        Assert.Equal(3, draft.Items.Count);
        // Varsayılan kategoriler olasılıktan: %40 Pipeline, %70 En İyi Durum, %90 Taahhüt.
        Assert.Equal("Pipeline", ItemOf(draft, a).Category);
        Assert.Equal("BestCase", ItemOf(draft, b).Category);
        Assert.Equal("Commit", ItemOf(draft, hot).Category);
        Assert.Equal(150_000m, draft.Scenarios!.Realized);
        Assert.Equal(200_000m, draft.Scenarios.CommitScenario);       // 150.000 + 50.000
        Assert.Equal(400_000m, draft.Scenarios.OptimisticScenario);   // + 200.000 (en iyi durum)
        // Ağırlıklı ayrı yöntem: 40.000 + 140.000 + 45.000 = 225.000 (+150.000 gerçekleşen).
        Assert.Equal(225_000m, draft.Scenarios.WeightedOpen);
        Assert.Equal(375_000m, draft.Scenarios.WeightedScenario);

        // Düzeltme gerekçe ister; kaynak fırsat tutarı değişmez.
        var itemB = ItemOf(draft, b);
        var noReason = await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Category", "Commit", ""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var afterCategory = await AdjustAsync(s.Admin, draft.Header.Id, itemB.Id, "Category", "Commit", "Müşteri sözlü onay verdi");
        var afterAmount = await AdjustAsync(s.Admin, draft.Header.Id, itemB.Id, "Amount", "-20000", "İskonto bekleniyor");
        var adjusted = await AdjustAsync(s.Admin, draft.Header.Id, itemB.Id, "Probability", "95", "Sözleşme imzada");
        Assert.Equal(3, adjusted.Adjustments.Count);
        var changed = ItemOf(adjusted, b);
        Assert.Equal(("BestCase", "Commit", 200_000m, -20_000m, 180_000m), (changed.BaseCategory, changed.Category, changed.BaseAmount, changed.AdjustmentAmount, changed.FinalAmount));
        Assert.Equal((70, 95, "Override"), (changed.Probability, changed.EffectiveProbability, changed.ProbabilitySource));
        Assert.Equal(380_000m, afterAmount.Scenarios!.CommitScenario);
        Assert.Equal(380_000m, adjusted.Scenarios!.CommitScenario);   // 150.000 + 50.000 + 180.000
        Assert.Equal(("Commit", 2), (adjusted.ByCategory.Single(c => c.Count == 2).Category, adjusted.ByCategory.Single(c => c.Count == 2).Count));
        Assert.NotNull(afterCategory);
        var opportunityAfter = await s.Admin.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{b}");
        Assert.Equal((200_000m, 70), (opportunityAfter!.Amount, opportunityAfter.Probability));

        // Geçersiz düzeltmeler.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Category", "Kesin", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Probability", "101", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Amount", "-999999", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Category", "Commit", "yine"))).StatusCode);

        // Akış: kilit için önce sunulmalı; sunulan tahmin değişmez; geri çekme gerekçe ister.
        Assert.Equal(HttpStatusCode.Conflict, (await LockAsync(s.Admin, draft.Header.Id)).StatusCode);
        var submitted = await ReadAsync<ForecastDetailDto>(await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/submit", new ForecastVersionRequestDto()));
        Assert.Equal("Submitted", submitted.Header.Status);
        Assert.Equal(HttpStatusCode.Conflict,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/adjust", new AdjustForecastRequestDto(itemB.Id, "Category", "Pipeline", "geç kaldık"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/refresh", new ForecastVersionRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/return", new ForecastReasonRequestDto(" "))).StatusCode);
        var back = await ReadAsync<ForecastDetailDto>(await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/return", new ForecastReasonRequestDto("Rakam güncellenecek")));
        Assert.Equal(("Draft", "Rakam güncellenecek"), (back.Header.Status, back.Header.ReturnReason));
        await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/submit", new ForecastVersionRequestDto());

        // Kilit: gerçekleşen satış kilit anında saklanır; aynı sürümün tekrar kilitlenmesi çakışmadır.
        var locked = await ReadAsync<ForecastDetailDto>(await LockAsync(s.Admin, draft.Header.Id));
        Assert.Equal(("Locked", 150_000m), (locked.Header.Status, locked.Header.RealizedAtLock));
        Assert.Equal(HttpStatusCode.Conflict, (await LockAsync(s.Admin, draft.Header.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/submit", new ForecastVersionRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.DeleteAsync($"/api/crm/sales/forecasts/{draft.Header.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await s.Admin.PutAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/note", new ForecastNoteRequestDto("sonradan"))).StatusCode);
        Assert.Equal("Pending", locked.Accuracy.State);   // dönem sürüyor: doğruluk henüz hesaplanmaz

        // Kilitli sürüme yeni kur eklense de satır tutarı değişmez (kur satırda saklıdır).
        await PostAsync<ExchangeRateDto>(s.Admin, "/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 99m, anchor));
        var frozen = await GetForecastAsync(s.Admin, draft.Header.Id);
        Assert.Equal(locked.Items.Select(i => i.FinalAmount).Order(), frozen.Items.Select(i => i.FinalAmount).Order());

        // Yeniden açma: gerekçe zorunlu; yeni sürüm (Taslak) doğar, kilitli sürüm olduğu gibi kalır; açık sürüm varken tekrar açılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/reopen", new ForecastReasonRequestDto(null))).StatusCode);
        var reopened = await ReadAsync<ForecastDetailDto>(await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/reopen", new ForecastReasonRequestDto("Büyük müşteri ertelendi")));
        Assert.NotEqual(draft.Header.Id, reopened.Header.Id);
        Assert.Equal((2, "Draft", draft.Header.Id, "Büyük müşteri ertelendi"), (reopened.Header.VersionNo, reopened.Header.Status, reopened.Header.PreviousSubmissionId, reopened.Header.ReopenReason));
        Assert.Equal(3, reopened.Items.Count);
        Assert.Equal("Commit", ItemOf(reopened, b).Category);   // düzeltmeler yeni sürüme taşınır
        Assert.Equal(HttpStatusCode.Conflict,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/reopen", new ForecastReasonRequestDto("tekrar"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "Tenant", null, null))).StatusCode);

        var original = await GetForecastAsync(s.Admin, draft.Header.Id);
        Assert.Equal("Locked", original.Header.Status);
        Assert.Equal(380_000m, original.Scenarios!.CommitScenario);

        // Sürüm karşılaştırması: B kazanılıp A ileri aya ertelenince yeni sürümde ikisi de çıkar, nedenleriyle.
        var bOpportunity = (await s.Admin.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{b}"))!;
        Assert.True((await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{b}/win", new WinOpportunityRequestDto(bOpportunity.Version))).IsSuccessStatusCode);
        var aOpportunity = (await s.Admin.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{a}"))!;
        Assert.True((await s.Admin.PutAsJsonAsync($"/api/crm/opportunities/{a}",
            new SaveOpportunityRequest(aOpportunity.Title, aOpportunity.CompanyId, Amount: 100_000m, ExpectedCloseDate: anchor.AddMonths(2), Probability: 40, Version: aOpportunity.Version))).IsSuccessStatusCode);
        var refreshed = await ReadAsync<ForecastDetailDto>(await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{reopened.Header.Id}/refresh", new ForecastVersionRequestDto()));
        Assert.Equal(["Sıcak"], refreshed.Items.Select(i => i.Title));

        var comparison = await ReadAsync<ForecastComparisonDto>(await s.Admin.GetAsync($"/api/crm/sales/forecasts/{reopened.Header.Id}/compare"));
        Assert.Equal(draft.Header.Id, comparison.PreviousId);
        Assert.Equal("Won", comparison.Removed.Single(r => r.Title == "B").Outcome);
        Assert.Equal("Postponed", comparison.Removed.Single(r => r.Title == "A").Outcome);
        Assert.Empty(comparison.Added);

        // Eski sürüm refresh'ten etkilenmedi.
        Assert.Equal(3, (await GetForecastAsync(s.Admin, draft.Header.Id)).Items.Count);
    }

    [Fact]
    public async Task Forecast_marks_rows_without_rate_or_probability_and_keeps_them_out_of_totals()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanılan", 1m);
        await OpenAsync(s.Admin, s.CompanyId, "Kursuz", 1_000m, 80, ThisMonth(anchor), currency: "EUR");
        await OpenAsync(s.Admin, s.CompanyId, "Normal", 100m, 80, ThisMonth(anchor));

        var detail = await CreateForecastAsync(s.Admin, anchor);

        Assert.Contains("MissingRate", detail.Items.Single(i => i.Title == "Kursuz").Flags);
        Assert.Null(detail.Items.Single(i => i.Title == "Kursuz").BaseAmount);
        // Kursuz satır hiçbir toplamda yok (kur 1 varsayılmaz).
        Assert.Equal(100m, detail.Scenarios!.Commit);
        Assert.Equal(80m, detail.Scenarios.WeightedOpen);
        Assert.Contains(detail.Warnings, w => w.Contains("kuru yoktu"));

        // Tutarı olmayan (kursuz) satırın tutarı düzeltilemez.
        var item = detail.Items.Single(i => i.Title == "Kursuz");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{detail.Header.Id}/adjust", new AdjustForecastRequestDto(item.Id, "Amount", "10", "deneme"))).StatusCode);

        // Kur eklenip taslak yenilenince satır toplama girer; yönetici kategori düzeltmesi yenilemede korunur.
        await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{detail.Header.Id}/adjust", new AdjustForecastRequestDto(detail.Items.Single(i => i.Title == "Normal").Id, "Category", "Omitted", "Hariç tutuldu"));
        await PostAsync<ExchangeRateDto>(s.Admin, "/api/crm/exchange-rates", new SaveExchangeRateRequest("EUR", "TRY", 35m, anchor.AddDays(-3)));
        var refreshed = await ReadAsync<ForecastDetailDto>(await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{detail.Header.Id}/refresh", new ForecastVersionRequestDto()));
        Assert.Equal(35_000m, refreshed.Items.Single(i => i.Title == "Kursuz").BaseAmount);
        Assert.Equal("Omitted", refreshed.Items.Single(i => i.Title == "Normal").Category);
        Assert.Equal(35_000m, refreshed.Scenarios!.Commit);
    }

    [Fact]
    public async Task Forecast_actions_need_their_own_permissions_and_a_complete_data_scope()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanılan", 10m);
        await OpenAsync(s.Admin, s.CompanyId, "Açık", 1_000m, 85, ThisMonth(anchor));
        var draft = await CreateForecastAsync(s.Admin, anchor);

        var writer = await UserAsync(s, [OpportunitiesRead, OpportunityAmountsRead, ForecastsRead, ForecastsWrite], Scope.All);
        var submitter = await UserAsync(s, [OpportunitiesRead, OpportunityAmountsRead, ForecastsRead, ForecastsSubmit], Scope.All);
        var reader = await UserAsync(s, [OpportunitiesRead, OpportunityAmountsRead, ForecastsRead], Scope.All);
        var noAmounts = await UserAsync(s, [OpportunitiesRead, ForecastsRead, ForecastsWrite], Scope.All);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "Tenant", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/submit", new ForecastVersionRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/refresh", new ForecastVersionRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await submitter.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/lock", new ForecastVersionRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/reopen", new ForecastReasonRequestDto("x"))).StatusCode);

        // Tutar izni yok → tahmin hazırlanamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await noAmounts.PostAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/refresh", new ForecastVersionRequestDto())).StatusCode);
        // Okuyucu satırları ve toplamı görür ama kapabiliteleri yoktur.
        var seen = await GetForecastAsync(reader, draft.Header.Id);
        Assert.Single(seen.Items);
        Assert.False(seen.Capabilities.CanWrite || seen.Capabilities.CanSubmit || seen.Capabilities.CanLock);
        // Fırsat tutarı izni olmayan okuyucu satırları hiç görmez.
        var blind = await UserAsync(s, [ForecastsRead], Scope.All);
        var blindView = await GetForecastAsync(blind, draft.Header.Id);
        Assert.False(blindView.ItemsVisible);
        Assert.Empty(blindView.Items);
        Assert.Null(blindView.Scenarios);

        // Veri kapsamı Kendi olan kullanıcı tenant tahmini hazırlayamaz (anlık görüntü eksik kalırdı); kendi kullanıcı tahminini hazırlar.
        var scoped = await UserWithIdAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead, ForecastsRead, ForecastsWrite], Scope.Own);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.Client.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "Tenant", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.Client.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "User", s.AdminUser.Id, null))).StatusCode);
        var mine = await ReadAsync<ForecastDetailDto>(await scoped.Client.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "User", scoped.UserId, null)), HttpStatusCode.Created);
        Assert.Empty(mine.Items);
        // Kapsamı dar kullanıcı, kapsamı geniş tahmin satırlarını göremez ve tenant tahmininin gerçekleşenini görmez.
        var limited = await GetForecastAsync(scoped.Client, draft.Header.Id);
        Assert.Empty(limited.Items);
        Assert.Null(limited.Realized);
        Assert.Contains(limited.Warnings, w => w.Contains("veri kapsamınızın dışında"));

        // Genel kayıt ucundan tahmin oluşturulamaz; durum alanı da yazılamaz.
        var generic = await s.Admin.PostAsJsonAsync("/api/records/crm.forecast-submission", new { values = new { status = "Locked", periodStart = "2020-01-01" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, generic.StatusCode);
    }

    [Fact]
    public async Task Forecast_creation_validates_scope_period_and_currency_and_deleting_a_draft_removes_it()
    {
        var s = await SetUpAsync();
        var anchor = await WinAsync(s.Admin, s.CompanyId, "Kazanılan", 10m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Haftalık", anchor, "Tenant", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "Takım", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "User", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "User", Guid.NewGuid(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", anchor, "Tenant", null, "TL"))).StatusCode);

        // Çeyrek ve yıl dönemi mali takvime göre kurulur (varsayılan Ocak); aynı slot ikinci kez açılamaz.
        var quarter = await CreateForecastAsync(s.Admin, anchor, "Quarter");
        Assert.Equal(quarter.Header.PeriodStart.AddMonths(3).AddDays(-1), quarter.Header.PeriodEnd);
        Assert.Equal(0, (quarter.Header.PeriodStart.Month - 1) % 3);

        var draft = await CreateForecastAsync(s.Admin, anchor);
        var note = await ReadAsync<ForecastDetailDto>(await s.Admin.PutAsJsonAsync($"/api/crm/sales/forecasts/{draft.Header.Id}/note", new ForecastNoteRequestDto("Yönetim notu")));
        Assert.Equal("Yönetim notu", note.Header.Note);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/crm/sales/forecasts/{draft.Header.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"/api/crm/sales/forecasts/{draft.Header.Id}")).StatusCode);
        Assert.Equal(0, await factory.ScalarAsync<int>(s.TenantId, "SELECT COUNT(*) FROM [crm].[ForecastItems] WHERE [SubmissionId] = @id", ("@id", draft.Header.Id)));
    }

    // ---- Yardımcılar

    private record Setup(HttpClient Admin, ModulixUser AdminUser, Tenant Tenant, Guid CompanyId)
    {
        public Guid TenantId => Tenant.Id;
    }

    private record Scoped(HttpClient Client, Guid UserId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ManagerPermissions, Scope.All, admin);
        var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return new Setup(client, admin, tenant, company.Id);
    }

    private async Task<HttpClient> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope) => (await UserWithIdAsync(s, permissions, scope)).Client;

    private async Task<Scoped> UserWithIdAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, permissions, scope, user);
        return new Scoped(await LoginAsync(user), user.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static DateOnly ThisMonth(DateOnly anchor) => new(anchor.Year, anchor.Month, 28);

    private static (DateOnly Start, DateOnly End) MonthRange(DateOnly anchor) =>
        (new DateOnly(anchor.Year, anchor.Month, 1), new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1));

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<Guid> OpenAsync(
        HttpClient client, Guid companyId, string title, decimal amount, int? probability, DateOnly close, string currency = "TRY") =>
        (await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, Currency: currency, ExpectedCloseDate: close, Probability: probability))).Id;

    /// <summary>Fırsat açar ve kazanıldı olarak kapatır; sunucunun kapanış günü (raporun dönem çapası) döner.</summary>
    private static async Task<DateOnly> WinAsync(HttpClient client, Guid companyId, string title, decimal amount)
    {
        var opportunity = await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow)));
        var result = await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(opportunity.Version)));
        return result.Opportunity.ActualCloseDate!.Value;
    }

    private static async Task<SalesOverviewDto> OverviewAsync(HttpClient client, DateOnly anchor) =>
        await ReadAsync<SalesOverviewDto>(await client.GetAsync($"/api/crm/sales/overview?kind=Month&date={anchor:yyyy-MM-dd}"));

    private static async Task<SalesCardDetailDto> DetailAsync(HttpClient client, string card, DateOnly anchor) =>
        await ReadAsync<SalesCardDetailDto>(await client.GetAsync($"/api/crm/sales/overview/cards/{card}?kind=Month&date={anchor:yyyy-MM-dd}"));

    private static async Task<SalesTargetDto> CreateTargetAsync(HttpClient client, SaveSalesTargetRequest request) =>
        await PostAsync<SalesTargetDto>(client, "/api/crm/sales-targets", request);

    private static async Task<ForecastDetailDto> CreateForecastAsync(HttpClient client, DateOnly anchor, string kind = "Month") =>
        await ReadAsync<ForecastDetailDto>(await client.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto(kind, anchor, "Tenant", null, null)), HttpStatusCode.Created);

    private static async Task<ForecastDetailDto> GetForecastAsync(HttpClient client, Guid id) =>
        await ReadAsync<ForecastDetailDto>(await client.GetAsync($"/api/crm/sales/forecasts/{id}"));

    private static async Task<ForecastDetailDto> AdjustAsync(HttpClient client, Guid id, Guid itemId, string kind, string value, string reason) =>
        await ReadAsync<ForecastDetailDto>(await client.PostAsJsonAsync($"/api/crm/sales/forecasts/{id}/adjust", new AdjustForecastRequestDto(itemId, kind, value, reason)));

    private static Task<HttpResponseMessage> LockAsync(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"/api/crm/sales/forecasts/{id}/lock", new ForecastVersionRequestDto());

    private static ForecastItemDto ItemOf(ForecastDetailDto detail, Guid opportunityId) => detail.Items.Single(i => i.OpportunityId == opportunityId);
}
