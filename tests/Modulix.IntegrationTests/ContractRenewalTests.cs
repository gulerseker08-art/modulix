using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Contracts;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 8: yenileme, fesih ve zamanlanmış işler. Mutlu yol (eşik hatırlatmaları, otomatik uzatma, taslak, yürürlük geçişleri), doğrulama (karar kuralları, fesih),
/// idempotency ve kaçırılan işler (tekrar tur, eşzamanlı işçiler, kesinti sonrası birikmiş eşikler), tenant izolasyonu ve yetki (yenileme/fesih ayrı izinler).
/// </summary>
public class ContractRenewalTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Contracts = "/api/contracts/contracts";

    private sealed record Setup(Tenant Tenant, ModulixUser Admin, ModulixUser Manager, ModulixUser Terminator, ModulixUser Reader, ModulixUser Owner, ModulixUser Escalation, Guid PartyId);

    [Fact]
    public async Task Reminders_follow_the_thresholds_skip_missed_ones_after_an_outage_escalate_once_and_never_repeat()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        // Bitişe 25 gün, bildirim süresi 10 gün → son bildirim 15 gün sonra. 90, 60 ve 30 eşikleri birikmiş: yalnızca 30 için aktivite, 90 ve 60 "atlandı".
        var contract = await ActiveAsync(admin, s, endIn: 25, notice: 10, owner: s.Owner.Id, escalation: s.Escalation.Id);

        var first = await RunAsync(s.Tenant.Id);
        Assert.Equal((1, 2, 0), (first.RemindersCreated, first.RemindersSkipped, first.EscalationsCreated));
        var overview = (await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{contract.Id}/renewal"))!;
        Assert.Equal([30], overview.Reminders.Where(r => r.Status == "Created").Select(r => r.ThresholdDays));
        Assert.Equal([60, 90], overview.Reminders.Where(r => r.Status == "Skipped").Select(r => r.ThresholdDays).Order());
        var reminder = (await ActivitiesAsync(admin)).Single(a => a.Subject.StartsWith("Sözleşme yenileme"));
        Assert.Equal(s.Owner.Id, reminder.OwnerId);
        Assert.Contains(contract.Number, reminder.Subject);

        // Aynı tur tekrar çalışırsa hiçbir şey üretmez (idempotent).
        var again = await RunAsync(s.Tenant.Id);
        Assert.Equal((0, 0, 0), (again.RemindersCreated, again.RemindersSkipped, again.EscalationsCreated));
        Assert.Single(await ActivitiesAsync(admin), a => a.Subject.StartsWith("Sözleşme yenileme"));

        // Zaman ilerler (bitiş bugün+14, son bildirim bugün+4): 7 gün eşiği düşer. Son bildirim tarihi bitiş − bildirim süresinden türetilir; kayıt güncellenince yeniden hesaplanır.
        await SqlAsync(s.Tenant.Id, "UPDATE contracts.Contracts SET EndDate = {1}, NoticeDeadline = {2} WHERE Id = {0}", contract.Id, Today().AddDays(14), Today().AddDays(4));
        Assert.Equal(1, (await RunAsync(s.Tenant.Id)).RemindersCreated);

        // Son bildirim tarihi geçti ve karar yok: eskalasyon kullanıcısına tek aktivite.
        await SqlAsync(s.Tenant.Id, "UPDATE contracts.Contracts SET EndDate = {1}, NoticeDeadline = {2} WHERE Id = {0}", contract.Id, Today().AddDays(9), Today().AddDays(-1));
        var escalated = await RunAsync(s.Tenant.Id);
        Assert.Equal(1, escalated.EscalationsCreated);
        Assert.Equal(0, (await RunAsync(s.Tenant.Id)).EscalationsCreated);
        var escalation = (await ActivitiesAsync(admin)).Single(a => a.Subject.StartsWith("Yenileme kararı gecikti"));
        Assert.Equal(s.Escalation.Id, escalation.OwnerId);
        var final = (await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{contract.Id}/renewal"))!;
        Assert.Contains(final.Reminders, r => r.Kind == "Escalation" && r.Status == "Created");
        // Son bildirim tarihi geçmiş dönem için eşik hatırlatması üretilmez; yalnızca eskalasyon vardır.
        Assert.DoesNotContain(final.Reminders, r => r.Kind == "Renewal" && r.PeriodKey == final.EndDate);
    }

    [Fact]
    public async Task Automatic_extension_needs_the_tenant_policy_extends_by_calendar_months_catches_up_missed_periods_and_runs_once_even_with_concurrent_workers()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var auto = await ActiveAsync(admin, s, endIn: 0, notice: 30, mode: "Automatic", renewalMonths: 12);
        var manual = await ActiveAsync(admin, s, endIn: 0, notice: 30, mode: "Manual", renewalMonths: 12);

        // Açık politika yokken (varsayılan) hiçbir sözleşme kendiliğinden uzatılmaz.
        Assert.Equal(0, (await RunAsync(s.Tenant.Id)).AutoRenewed);
        Assert.Equal(Today(), (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{auto.Id}"))!.EndDate);
        var blocked = (await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{auto.Id}/renewal"))!;
        Assert.Contains("izin vermiyor", blocked.AutoRenewalBlockedReason);

        // Politika açılınca yalnızca otomatik yenilemeli sözleşme uzatılır: takvim ayı (sabit 365 gün değil).
        await SetSettingAsync(admin, "contracts.renewal.allow-auto", "true");
        var run = await RunAsync(s.Tenant.Id);
        Assert.Equal(1, run.AutoRenewed);
        var extended = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{auto.Id}"))!;
        Assert.Equal(Today().AddDays(1).AddMonths(12).AddDays(-1), extended.EndDate);
        Assert.Equal(extended.EndDate!.Value.AddDays(-30), extended.NoticeDeadline);
        Assert.Equal(Today(), (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{manual.Id}"))!.EndDate);
        var decision = Assert.Single((await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{auto.Id}/renewal"))!.History);
        Assert.Equal(("AutoExtended", "Scheduler", extended.EndDate), (decision.Outcome, decision.Source, decision.NewEndDate));
        Assert.Contains((await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{auto.Id}/history"))!, h => h.Kind == "AutoRenewed");

        // Tekrar tur çift uzatma yapmaz.
        Assert.Equal(0, (await RunAsync(s.Tenant.Id)).AutoRenewed);

        // Kesinti: aylık yenilemeli sözleşmenin dönemi 70 gün önce bitmiş; sırayla kaçırılan dönemler tamamlanır ve bitiş bugünden sonraya ulaşır.
        var outage = await ActiveAsync(admin, s, endIn: -70, notice: 5, mode: "Automatic", renewalMonths: 1);
        var catchUp = await RunAsync(s.Tenant.Id);
        Assert.InRange(catchUp.AutoRenewed, 3, 4);
        var caught = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{outage.Id}"))!;
        Assert.True(caught.EndDate >= Today());
        Assert.Equal(catchUp.AutoRenewed, (await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{outage.Id}/renewal"))!.History.Count);

        // Eşzamanlı işçiler: ikinci tur kilitte atlanır; yinelenen dönem kararı oluşmaz.
        var racing = await ActiveAsync(admin, s, endIn: 0, notice: 5, mode: "Automatic", renewalMonths: 6);
        var results = await Task.WhenAll(RunWithLockStateAsync(s.Tenant.Id), RunWithLockStateAsync(s.Tenant.Id), RunWithLockStateAsync(s.Tenant.Id));
        Assert.Equal(1, results.Sum(r => r.Run?.AutoRenewed ?? 0));
        Assert.Single((await admin.GetFromJsonAsync<RenewalOverviewDto>($"{Contracts}/{racing.Id}/renewal"))!.History);
    }

    [Fact]
    public async Task Declining_blocks_auto_extension_and_reminders_and_a_renewal_draft_copies_the_contract_goes_through_approval_and_is_never_duplicated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        await SetSettingAsync(admin, "contracts.renewal.allow-auto", "true");

        // Yenilememe kararı: neden listeden, gerekçe notla; bir dönem için tek karar; otomatik uzatma ve hatırlatma durur.
        var declined = await ActiveAsync(admin, s, endIn: 0, notice: 30, mode: "Automatic", renewalMonths: 12, owner: s.Owner.Id);
        Assert.Contains("reason", (await ValidationProblemAsync(await manager.PostAsJsonAsync($"{Contracts}/{declined.Id}/renewal/decline", new DeclineRenewalRequest("Yok")))).Errors.Keys);
        var after = await ReadAsync<RenewalOverviewDto>(await manager.PostAsJsonAsync($"{Contracts}/{declined.Id}/renewal/decline", new DeclineRenewalRequest("Price", "Rakip daha ucuz")));
        Assert.Equal(("NotRenewed", "Price"), (after.CurrentDecision!.Outcome, after.CurrentDecision.Reason));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Contracts}/{declined.Id}/renewal/decline", new DeclineRenewalRequest("Other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Contracts}/{declined.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
        var run = await RunAsync(s.Tenant.Id);
        Assert.Equal((0, 0), (run.AutoRenewed, run.RemindersCreated));
        Assert.Equal(Today(), (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{declined.Id}"))!.EndDate);

        // Taslak: kaynak sözleşmeden kopya, önceki sözleşme bağı, yeni dönem (bitişin ertesi günü + takvim ayı), kalemler aynı; onay süreci baştan.
        var source = await ActiveAsync(admin, s, endIn: 30, notice: 10, mode: "Manual", renewalMonths: 12, lines: 2);
        var draftResponse = await manager.PostAsJsonAsync($"{Contracts}/{source.Id}/renewal/draft", new RenewalDraftRequest());
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        var draft = (await draftResponse.Content.ReadFromJsonAsync<RenewalDraftResultDto>())!;
        Assert.False(draft.AlreadyExisted);
        var copy = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{draft.ContractId}"))!;
        var sourceStart = Today().AddDays(31);
        Assert.Equal((source.Id, sourceStart, sourceStart.AddMonths(12).AddDays(-1), "Draft", "NotRequired", "NotInForce"),
            (copy.PreviousContractId, copy.StartDate, copy.EndDate, copy.PreparationStatus, copy.ApprovalStatus, copy.EffectiveStatus));
        var lines = (await admin.GetFromJsonAsync<PagedResult<ContractLineDto>>($"/api/contracts/contract-lines?contractId={draft.ContractId}"))!.Items;
        var sourceLines = (await admin.GetFromJsonAsync<PagedResult<ContractLineDto>>($"/api/contracts/contract-lines?contractId={source.Id}"))!.Items;
        Assert.Equal(sourceLines.Select(l => (l.Name, l.UnitPrice, l.Quantity)).Order(), lines.Select(l => (l.Name, l.UnitPrice, l.Quantity)).Order());
        Assert.Equal(sourceLines.Sum(l => l.Gross), lines.Sum(l => l.Gross));

        // Tekrarlanan istek aynı taslağı döndürür; mükerrer sözleşme üretilmez.
        var repeat = await manager.PostAsJsonAsync($"{Contracts}/{source.Id}/renewal/draft", new RenewalDraftRequest());
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal((draft.ContractId, true), ((await repeat.Content.ReadFromJsonAsync<RenewalDraftResultDto>())!.ContractId, true));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => manager.PostAsJsonAsync($"{Contracts}/{source.Id}/renewal/draft", new RenewalDraftRequest())));
        Assert.All(parallel, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Single((await admin.GetFromJsonAsync<PagedResult<ContractDto>>($"{Contracts}?previousContractId={source.Id}"))!.Items);

        // Zincir: önceki, bu ve ardıl; fiyat değişikliği taslakta yapılır ve normal onaya gider (onay durumu "gerekmiyor"dan başlar, gönderimde kurallar uygulanır).
        var chain = (await admin.GetFromJsonAsync<List<ChainItemDto>>($"{Contracts}/{draft.ContractId}/chain"))!;
        Assert.Equal([("Predecessor", source.Id), ("Current", draft.ContractId)], chain.Select(c => (c.Relation, c.Id)));
        Assert.Equal(["Current", "Successor"], (await admin.GetFromJsonAsync<List<ChainItemDto>>($"{Contracts}/{source.Id}/chain"))!.Select(c => c.Relation));

        // Takvim: kaynak sözleşme karar ve taslakla görünür.
        var calendar = (await admin.GetFromJsonAsync<List<RenewalCalendarItemDto>>("/api/contracts/renewals/calendar?days=90"))!;
        Assert.Equal(("DraftCreated", draft.ContractId), (calendar.Single(c => c.ContractId == source.Id).Decision, calendar.Single(c => c.ContractId == source.Id).ResultContractId!.Value));
        Assert.Equal("NotRenewed", calendar.Single(c => c.ContractId == declined.Id).Decision);
    }

    [Fact]
    public async Task Termination_is_a_separate_critical_permission_with_notice_confirmation_scheduling_and_commitment_cleanup_and_dates_move_the_contract_through_its_life()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var terminator = await LoginAsync(s.Terminator);
        using var manager = await LoginAsync(s.Manager);
        var contract = await ActiveAsync(admin, s, endIn: 200, notice: 30);
        var commitment = await PostAsync<ContractCommitmentDto>(admin, "/api/contracts/commitments", new SaveContractCommitmentRequest(contract.Id, "Aylık rapor", Today().AddDays(-1), Today().AddDays(20)));

        // Yetki: yenileme yöneticisi feshedemez; doğrulama: gerekçe, tarih aralığı, imzasız sözleşme.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(60), "x"))).StatusCode);
        Assert.Contains("reason", (await ValidationProblemAsync(await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(60), " ")))).Errors.Keys);
        Assert.Contains("effectiveDate", (await ValidationProblemAsync(await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(400), "geç")))).Errors.Keys);
        var draftContract = await PostAsync<ContractDto>(admin, Contracts, new SaveContractRequest("Taslak", s.PartyId, StartDate: Today(), EndDate: Today().AddDays(100)));
        Assert.Equal(HttpStatusCode.Conflict, (await terminator.PostAsJsonAsync($"{Contracts}/{draftContract.Id}/terminate", new TerminateContractRequest(Today().AddDays(10), "imzasız"))).StatusCode);

        // Bildirim süresinden (30 gün) kısa fesih onay ister.
        var shortNotice = await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(10), "Acil fesih"));
        Assert.Equal(HttpStatusCode.Conflict, shortNotice.StatusCode);
        Assert.Contains("bildirim süresinin", await shortNotice.Content.ReadAsStringAsync());

        // Gelecek tarihli fesih planlanır: sözleşme yürürlükte kalır; tarih gelince zamanlayıcı feshedilmiş yapar ve taahhütleri kapatır.
        var scheduled = await ReadAsync<RenewalOverviewDto>(await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(60), "Müşteri talebi")));
        Assert.Equal(("Active", Today().AddDays(60), "Müşteri talebi"), (scheduled.EffectiveStatus, scheduled.TerminationDate, scheduled.TerminationReason));
        Assert.Equal(0, (await RunAsync(s.Tenant.Id)).Terminated);
        await SqlAsync(s.Tenant.Id, "UPDATE contracts.Contracts SET TerminationDate = {1} WHERE Id = {0}", contract.Id, Today().AddDays(-1));
        Assert.Equal(1, (await RunAsync(s.Tenant.Id)).Terminated);
        var terminated = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        Assert.Equal("Terminated", terminated.EffectiveStatus);
        Assert.Equal("Cancelled", (await admin.GetFromJsonAsync<ContractCommitmentDto>($"/api/contracts/commitments/{commitment.Id}"))!.Status);
        Assert.Contains((await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{contract.Id}/history"))!, h => h.Kind == "Terminated");
        Assert.Equal(HttpStatusCode.Conflict, (await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today(), "tekrar"))).StatusCode);

        // Hemen fesih (bugün/geçmiş tarih) anında etkilidir.
        var immediate = await ActiveAsync(admin, s, endIn: 90, notice: 0);
        var done = await ReadAsync<RenewalOverviewDto>(await terminator.PostAsJsonAsync($"{Contracts}/{immediate.Id}/terminate", new TerminateContractRequest(Today(), "Anlaşmalı fesih")));
        Assert.Equal("Terminated", done.EffectiveStatus);

        // Tarihe bağlı yürürlük geçişleri: başlangıcı gelen aktifleşir, bitişi geçen süresi dolar; süresi dolan geri aktifleşmez.
        var waiting = await ActiveAsync(admin, s, endIn: 90, notice: 0, status: "AwaitingStart");
        var lapsed = await ActiveAsync(admin, s, endIn: -2, notice: 0, mode: "None");
        var moved = await RunAsync(s.Tenant.Id);
        Assert.Equal((1, 1), (moved.Activated, moved.PeriodsExpired));
        Assert.Equal("Active", (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{waiting.Id}"))!.EffectiveStatus);
        Assert.Equal("Expired", (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{lapsed.Id}"))!.EffectiveStatus);
        Assert.Equal((0, 0), ((await RunAsync(s.Tenant.Id)).Activated, (await RunAsync(s.Tenant.Id)).PeriodsExpired));
    }

    [Fact]
    public async Task Tenants_cannot_see_decide_or_run_each_others_renewals_and_the_scheduler_only_touches_its_own_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var managerB = await LoginAsync(b.Manager);
        using var terminatorB = await LoginAsync(b.Terminator);
        await SetSettingAsync(adminA, "contracts.renewal.allow-auto", "true");
        await SetSettingAsync(adminB, "contracts.renewal.allow-auto", "true");
        var contract = await ActiveAsync(adminA, a, endIn: 0, notice: 10, mode: "Automatic", renewalMonths: 12);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Contracts}/{contract.Id}/renewal")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/decline", new DeclineRenewalRequest("Price"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await terminatorB.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today(), "sızıntı"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Contracts}/{contract.Id}/chain")).StatusCode);
        Assert.DoesNotContain((await adminB.GetFromJsonAsync<List<RenewalCalendarItemDto>>("/api/contracts/renewals/calendar?days=90"))!, c => c.ContractId == contract.Id);

        // B'nin turu A'nın sözleşmesini uzatmaz; A'nın turu yalnızca A'da uzatır.
        Assert.Equal(0, (await RunAsync(b.Tenant.Id)).AutoRenewed);
        Assert.Equal(Today(), (await adminA.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!.EndDate);
        Assert.Equal(1, (await RunAsync(a.Tenant.Id)).AutoRenewed);

        // Sözleşme modülü kapalı tenant için tur çalışmaz.
        var closed = await factory.CreateTenantAsync();
        Assert.Null((await factory.Services.GetRequiredService<ContractJobsRunner>().RunTenantAsync(closed.Id, CancellationToken.None)).Run);
    }

    [Fact]
    public async Task Renewal_decisions_termination_and_the_manual_scheduler_run_need_their_own_permissions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var manager = await LoginAsync(s.Manager);
        using var terminator = await LoginAsync(s.Terminator);
        var contract = await ActiveAsync(admin, s, endIn: 60, notice: 30);

        // Okuma serbest (sözleşme görünürlüğü); karar, taslak, fesih ve elle tur yetki ister.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Contracts}/{contract.Id}/renewal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/contracts/renewals/calendar?days=90")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/decline", new DeclineRenewalRequest("Price"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/decline", new DeclineRenewalRequest("Price"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminator.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/terminate", new TerminateContractRequest(Today().AddDays(90), "yetkisiz"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/contracts/scheduler/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync("/api/contracts/scheduler/run", null)).StatusCode);

        // Taslak, fiyatların kopyalanması nedeniyle yazma ve finansal izin ister: yalnızca yenileme izni yetmez.
        using var renewalOnly = await LoginAsync(await RenewalOnlyUserAsync(s));
        Assert.Equal(HttpStatusCode.Forbidden, (await renewalOnly.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/renewal/draft", new RenewalDraftRequest())).StatusCode);
    }

    // ---- yardımcılar

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var terminator = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var owner = await factory.CreateUserAsync(tenant);
        var escalation = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsWrite, FinancialsRead, RenewalsManage], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsTerminate], Scope.All, terminator);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader, owner, escalation);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        return new Setup(tenant, admin, manager, terminator, reader, owner, escalation, party.Id);
    }

    private async Task<ModulixUser> RenewalOnlyUserAsync(Setup s)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.Tenant.Id, [ContractsRead, RenewalsManage], Scope.All, user);
        return user;
    }

    /// <summary>İmzalı ve yürürlükteki (ya da belirtilen yürürlük durumunda) sözleşme: kayıt API'den açılır, imza/yürürlük durumu doğrudan veritabanında kurulur (bu testler zamanlayıcıyı sınar).</summary>
    private async Task<ContractDto> ActiveAsync(
        HttpClient admin, Setup s, int endIn, int notice, string mode = "Manual", int? renewalMonths = null, Guid? owner = null, Guid? escalation = null, int lines = 1, string status = "Active")
    {
        var start = status == "AwaitingStart" ? Today() : Today().AddDays(-100);
        var contract = await PostAsync<ContractDto>(admin, Contracts, new SaveContractRequest(
            "Bakım sözleşmesi", s.PartyId, StartDate: start, EndDate: Today().AddDays(endIn), RenewalMode: mode, RenewalPeriodMonths: mode == "None" ? null : renewalMonths ?? 12,
            NoticePeriodDays: notice, OwnerId: owner, EscalationUserId: escalation));
        for (var i = 1; i <= lines; i++)
        {
            await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, $"Kalem {i}", Quantity: i, UnitPrice: 1000m * i, TaxRatePercent: 20m));
        }

        await SqlAsync(s.Tenant.Id,
            "UPDATE contracts.Contracts SET PreparationStatus = 'ReadyToPublish', ApprovalStatus = 'Approved', SignatureStatus = 'Completed', EffectiveStatus = {1}, EffectiveVersionNo = 1 WHERE Id = {0}",
            contract.Id, status);
        return (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
    }

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args.Select(Sql).ToArray()) >= 1);
    }

    private static object Sql(object value) => value switch
    {
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        _ => value,
    };

    private async Task<ContractSchedulerRun> RunAsync(Guid tenantId)
    {
        var (run, skipped) = await factory.Services.GetRequiredService<ContractJobsRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return run!;
    }

    private async Task<(ContractSchedulerRun? Run, bool Skipped)> RunWithLockStateAsync(Guid tenantId) =>
        await factory.Services.GetRequiredService<ContractJobsRunner>().RunTenantAsync(tenantId, CancellationToken.None);

    private static async Task<List<ActivityDto>> ActivitiesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<PagedResult<ActivityDto>>("/api/crm/activities?pageSize=200"))!.Items];

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).DateTime);

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<RecordResponse> PostRecordAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
