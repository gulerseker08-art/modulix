using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 5: iç onay. Mutlu yol (iki adımlı kural planı, sıralı ve paralel onay, vekâlet, geri gönderme), doğrulama (kural koşulları, vekâlet),
/// tenant izolasyonu (kurallar, vekâletler, onay planı, iş listesi) ve yetki (adım, dört göz, yönetim izni, iş listesi izni).
/// </summary>
public class ContractApprovalFlowTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Contracts = "/api/contracts/contracts";
    private const string Rules = "/api/contracts/approval-rules";
    private const string Delegations = "/api/contracts/approval-delegations";

    private sealed record Setup(Tenant Tenant, ModulixUser Admin, ModulixUser Writer, ModulixUser ApproverA, ModulixUser ApproverB, ModulixUser Delegate, ModulixUser Reader, Guid PartyId);

    [Fact]
    public async Task Two_step_plan_runs_in_order_dual_control_blocks_repeat_decisions_and_history_records_each_step()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        using var b = await LoginAsync(s.ApproverB);
        var amountRule = await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Yüksek tutar", Step: 1, MinGrossTotal: 1000m, Currency: "TRY", ApproverId: s.ApproverA.Id));
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Standart dışı madde", Step: 2, RequiresNonStandardTerms: true, ApproverId: s.ApproverB.Id));
        var contract = await ReadyAsync(admin, s.PartyId, nonStandard: true);

        contract = await RunAsync(admin, contract.Id, "submit-for-approval", contract.Version);
        Assert.Equal("Pending", contract.ApprovalStatus);

        // Plan gönderimde kurulur: iki kural, iki adım; neden metninde tutar yoktur.
        var plan = (await admin.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.Equal(2, plan.Rules.Count);
        Assert.Equal([1, 2], plan.Steps.Select(x => x.Step));
        Assert.Equal(1, plan.CurrentStep);
        Assert.All(plan.Rules, r => Assert.DoesNotContain("2.400", r.Reason + r.Name));
        Assert.Equal(amountRule.Version, plan.Rules.First(r => r.Step == 1).RuleVersion);
        Assert.NotNull(plan.DueAt);
        Assert.False(plan.IsOverdue);
        Assert.False(plan.CanDecide); // gönderen kendi onayını veremez (aynı zamanda adlı onaylayıcı değildir)

        // Sıra: ikinci adımdaki onaylayıcı birinci adımda karar veremez.
        var early = await b.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        var bActions = (await b.GetFromJsonAsync<List<ContractActionDto>>($"{Contracts}/{contract.Id}/actions"))!;
        var blocked = bActions.Single(x => x.Action == "approve");
        Assert.False(blocked.Allowed);
        Assert.Equal("not-your-step", blocked.Code);

        // Birinci adım onaylanır: sözleşme hâlâ onay bekler, ikinci adım açılır.
        var afterA = await RunAsync(a, contract.Id, "approve", contract.Version, "Tutar uygun");
        Assert.Equal("Pending", afterA.ApprovalStatus);
        Assert.True(afterA.Version > contract.Version);
        var mid = (await a.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.True(mid.Steps[0].Completed);
        Assert.Equal(2, mid.CurrentStep);
        Assert.Equal("Approved", Assert.Single(mid.Decisions).Decision);

        // Aynı kişi ikinci kez karar veremez; yanlış sürümle gönderilen karar 409 alır.
        var again = await a.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(afterA.Version));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        var stale = await b.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var done = await RunAsync(b, contract.Id, "approve", afterA.Version, "Madde kabul");
        Assert.Equal("Approved", done.ApprovalStatus);
        var final = (await b.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.All(final.Steps, st => Assert.True(st.Completed));
        Assert.Equal([1, 2], final.Decisions.Select(d => d.StepNo));
        var version = (await admin.GetFromJsonAsync<ContractVersionDto>($"{Contracts}/{contract.Id}/versions/1"))!;
        Assert.Equal("Approved", version.Status);

        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{contract.Id}/history"))!.Select(h => h.Kind).ToList();
        Assert.Contains("SubmittedForApproval", history);
        Assert.Contains("ApprovalStepApproved", history);
        Assert.Contains("Approved", history);
        // Gönderim tarihçesi eşleşen kuralları (ad, sürüm) yazar.
        Assert.Contains("Yüksek tutar", (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{contract.Id}/history"))!.First(h => h.Kind == "SubmittedForApproval").Reason);
    }

    [Fact]
    public async Task Parallel_named_approvers_in_one_step_all_have_to_approve_and_the_inbox_lists_only_decidable_contracts()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        using var b = await LoginAsync(s.ApproverB);
        using var reader = await LoginAsync(s.Reader);
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("A onayı", Step: 1, MinGrossTotal: 1000m, ApproverId: s.ApproverA.Id));
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("B onayı", Step: 1, RequiresNonStandardTerms: true, ApproverId: s.ApproverB.Id));
        var contract = await ReadyAsync(admin, s.PartyId, nonStandard: true);
        contract = await RunAsync(admin, contract.Id, "submit-for-approval", contract.Version);

        var inboxA = (await a.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox"))!;
        Assert.Equal(contract.Id, Assert.Single(inboxA).ContractId);
        Assert.True(inboxA[0].CanDecide);
        Assert.Equal(1, inboxA[0].StepCount);
        Assert.Equal(2, inboxA[0].WaitingFor.Count);

        contract = await RunAsync(a, contract.Id, "approve", contract.Version);
        Assert.Equal("Pending", contract.ApprovalStatus); // B de onaylamalı

        // A'nın iş listesinde artık karar bekleyen yok (kendi payı tamam); "tümü" görünümünde sözleşme durur ve karar verilemediği söylenir.
        Assert.Empty((await a.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox"))!);
        var all = (await a.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox?mine=false"))!;
        Assert.False(Assert.Single(all).CanDecide);
        Assert.Contains("zaten verildi", all[0].CannotDecideReason);
        Assert.Equal([s.ApproverB.Id], all[0].WaitingFor);

        contract = await RunAsync(b, contract.Id, "approve", contract.Version);
        Assert.Equal("Approved", contract.ApprovalStatus);
        Assert.Empty((await b.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox?mine=false"))!);

        // İş listesi onay iznini ister.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/contracts/approvals/inbox")).StatusCode);
    }

    [Fact]
    public async Task Delegate_decides_on_behalf_of_the_named_approver_only_while_the_delegation_is_active()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var del = await LoginAsync(s.Delegate);
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("A onayı", MinGrossTotal: 1000m, ApproverId: s.ApproverA.Id));

        var first = await SubmittedAsync(admin, s.PartyId);
        var denied = await del.PostAsJsonAsync($"{Contracts}/{first.Id}/approve", new ContractTransitionRequest(first.Version));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var delegation = await PostAsync<ContractApprovalDelegationDto>(admin, Delegations,
            new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.Delegate.Id, StartsAt: DateTimeOffset.UtcNow.AddHours(-1), Note: "İzinde"));
        var view = (await del.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{first.Id}/approval"))!;
        Assert.True(view.CanDecide);
        Assert.True(view.ActingAsDelegate);
        var approved = await RunAsync(del, first.Id, "approve", first.Version, "Vekâleten onay");
        Assert.Equal("Approved", approved.ApprovalStatus);
        var decision = Assert.Single((await del.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{first.Id}/approval"))!.Decisions);
        Assert.Equal(s.Delegate.Id, decision.ActorId);
        Assert.Contains(s.ApproverA.Id, decision.OnBehalfOf);

        // Vekâlet kapatılınca (ya da süresi dolunca) yeni sözleşmede delege onay veremez; geçmiş karar değişmez.
        var closed = await admin.PutAsJsonAsync($"{Delegations}/{delegation.Id}", new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.Delegate.Id, delegation.StartsAt, IsActive: false, Version: delegation.Version));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var second = await SubmittedAsync(admin, s.PartyId);
        Assert.Equal(HttpStatusCode.Forbidden, (await del.PostAsJsonAsync($"{Contracts}/{second.Id}/approve", new ContractTransitionRequest(second.Version))).StatusCode);

        var expiredRequest = new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.Delegate.Id, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1));
        await PostAsync<ContractApprovalDelegationDto>(admin, Delegations, expiredRequest);
        Assert.Equal(HttpStatusCode.Forbidden, (await del.PostAsJsonAsync($"{Contracts}/{second.Id}/approve", new ContractTransitionRequest(second.Version))).StatusCode);
    }

    [Fact]
    public async Task Return_for_changes_needs_a_reason_reopens_a_draft_revision_and_a_resubmission_starts_a_fresh_round()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        var contract = await SubmittedAsync(admin, s.PartyId);

        var noReason = await a.PostAsJsonAsync($"{Contracts}/{contract.Id}/return-for-changes", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var returned = await RunAsync(a, contract.Id, "return-for-changes", contract.Version, "Ödeme koşulu eksik");
        Assert.Equal(("Draft", "NotRequired", 2), (returned.PreparationStatus, returned.ApprovalStatus, returned.CurrentVersionNo));
        var versions = (await admin.GetFromJsonAsync<List<ContractVersionDto>>($"{Contracts}/{contract.Id}/versions"))!;
        Assert.Equal("Superseded", Assert.Single(versions).Status);
        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{contract.Id}/history"))!;
        var entry = history.Single(h => h.Kind == "ApprovalReturned");
        Assert.Equal("Ödeme koşulu eksik", entry.Reason);

        // Düzeltilip yeniden gönderilen sürümün onay turu temizdir: önceki tur kararı sayılmaz.
        returned = await RunAsync(admin, contract.Id, "send-to-review", returned.Version);
        returned = await RunAsync(admin, contract.Id, "mark-ready", returned.Version);
        returned = await RunAsync(admin, contract.Id, "submit-for-approval", returned.Version);
        var round = (await a.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.Equal(2, round.VersionNo);
        Assert.Empty(round.Decisions);
        Assert.True(round.CanDecide);

        // Geri çekip aynı sürümü yeniden göndermek de yeni tur açar; eski turun onayı yeni tura sayılmaz.
        returned = await RunAsync(a, contract.Id, "approve", returned.Version); // kural yok: tek adsız adım, onay tamamlanır
        Assert.Equal("Approved", returned.ApprovalStatus);
    }

    [Fact]
    public async Task Rejection_ends_the_round_and_a_withdrawn_round_does_not_carry_decisions_into_the_next_submission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        using var b = await LoginAsync(s.ApproverB);
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Adım 1", Step: 1, MinGrossTotal: 1000m, ApproverId: s.ApproverA.Id));
        await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Adım 2", Step: 2, MinGrossTotal: 1000m, ApproverId: s.ApproverB.Id));
        var contract = await SubmittedAsync(admin, s.PartyId);

        contract = await RunAsync(a, contract.Id, "approve", contract.Version);
        contract = await RunAsync(admin, contract.Id, "withdraw-approval", contract.Version, "Yanlış fiyat");
        Assert.Equal("Withdrawn", contract.ApprovalStatus);

        contract = await RunAsync(admin, contract.Id, "submit-for-approval", contract.Version);
        var fresh = (await a.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.Empty(fresh.Decisions);
        Assert.Equal(1, fresh.CurrentStep);
        Assert.False(fresh.Steps[0].Completed);

        contract = await RunAsync(a, contract.Id, "approve", contract.Version);
        var rejected = await RunAsync(b, contract.Id, "reject-approval", contract.Version, "Fiyat yüksek");
        Assert.Equal("Rejected", rejected.ApprovalStatus);
        var decisions = (await b.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!.Decisions;
        Assert.Equal(["Approved", "Rejected"], decisions.Select(d => d.Decision));
        // Sonuçlanmış onay yeniden reddedilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync($"{Contracts}/{contract.Id}/reject-approval", new ContractTransitionRequest(rejected.Version, "tekrar"))).StatusCode);
    }

    [Fact]
    public async Task Rule_and_delegation_validation_refuses_incomplete_or_inconsistent_definitions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        async Task<ValidationProblemDetails> RuleErrorAsync(SaveContractApprovalRuleRequest request) =>
            await ValidationProblemAsync(await admin.PostAsJsonAsync(Rules, request));

        Assert.Contains("minGrossTotal", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Koşulsuz"))).Errors.Keys);
        Assert.Contains("minGrossTotal", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Yalnızca tür", ContractType: "Service"))).Errors.Keys);
        Assert.Contains("step", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Adım", Step: 10, MinGrossTotal: 1m))).Errors.Keys);
        Assert.Contains("minDiscountPercent", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("İndirim", MinDiscountPercent: 100m))).Errors.Keys);
        Assert.Contains("currency", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Kur", MinGrossTotal: 1m, Currency: "TL"))).Errors.Keys);
        Assert.Contains("minRiskLevel", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Risk", MinRiskLevel: "Extreme"))).Errors.Keys);
        Assert.Contains("approverId", (await RuleErrorAsync(new SaveContractApprovalRuleRequest("Onaylayan", MinGrossTotal: 1m, ApproverId: Guid.NewGuid()))).Errors.Keys);

        var self = await ValidationProblemAsync(await admin.PostAsJsonAsync(Delegations, new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.ApproverA.Id)));
        Assert.Contains("toUserId", self.Errors.Keys);
        var reversed = await ValidationProblemAsync(await admin.PostAsJsonAsync(Delegations,
            new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.Delegate.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1))));
        Assert.Contains("endsAt", reversed.Errors.Keys);

        // Geçerli kural kaydedilir; para birimi küçük harfle de gelse büyütülür; tutar yoksa para birimi boşaltılır.
        var ok = await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Geçerli", MinGrossTotal: 5000m, Currency: "try"));
        Assert.Equal("TRY", ok.Currency);
        var noAmount = await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Tutarsız", RequiresNonStandardTerms: true, Currency: "USD"));
        Assert.Null(noAmount.Currency);
    }

    [Fact]
    public async Task Rule_changes_do_not_alter_the_plan_of_a_pending_approval_and_the_default_without_rules_is_one_unnamed_step()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var b = await LoginAsync(s.ApproverB);
        var rule = await PostAsync<ContractApprovalRuleDto>(admin, Rules, new SaveContractApprovalRuleRequest("Tutar", MinGrossTotal: 1000m, ApproverId: s.ApproverA.Id));
        var contract = await SubmittedAsync(admin, s.PartyId);

        // Kural sonradan kapatılıp onaylayıcısı değişse de bekleyen onayın planı dondurulmuştur.
        var changed = await admin.PutAsJsonAsync($"{Rules}/{rule.Id}", new SaveContractApprovalRuleRequest("Tutar", IsActive: false, MinGrossTotal: 1000m, ApproverId: s.ApproverB.Id, Version: rule.Version));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var plan = (await admin.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        var match = Assert.Single(plan.Rules);
        Assert.Equal((rule.Version, s.ApproverA.Id), (match.RuleVersion, match.ApproverId));
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(contract.Version))).StatusCode);

        // Etkin kural yokken yeni gönderim tek, adsız adımdır ve onay izni olan biri onaylar.
        var open = await SubmittedAsync(admin, s.PartyId);
        var openPlan = (await b.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{open.Id}/approval"))!;
        Assert.Empty(openPlan.Rules);
        Assert.Contains("gönüllü onay", openPlan.Note);
        Assert.Equal("Approved", (await RunAsync(b, open.Id, "approve", open.Version)).ApprovalStatus);
    }

    [Fact]
    public async Task Overdue_approvals_are_flagged_and_the_due_target_follows_the_tenant_setting()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        var contract = await SubmittedAsync(admin, s.PartyId);

        var plan = (await a.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!;
        Assert.InRange(plan.DueAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromHours(71), TimeSpan.FromHours(73));
        Assert.False(plan.IsOverdue);

        // Hedef tarih geçmişe çekilince onay gecikmiş görünür (iş listesinde ve sözleşmede); onayı engellemez.
        using (var scope = factory.OpenScope(s.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE contracts.ContractVersions SET ApprovalDueAt = DATEADD(hour, -5, SYSDATETIMEOFFSET()) WHERE ContractId = {0}", contract.Id);
        }

        Assert.True((await a.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{contract.Id}/approval"))!.IsOverdue);
        var inbox = (await a.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox"))!;
        Assert.True(Assert.Single(inbox).IsOverdue);
        Assert.Equal("Approved", (await RunAsync(a, contract.Id, "approve", contract.Version)).ApprovalStatus);
    }

    [Fact]
    public async Task Tenants_cannot_read_each_others_rules_delegations_plans_or_inbox()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var approverB = await LoginAsync(b.ApproverA);
        var rule = await PostAsync<ContractApprovalRuleDto>(adminA, Rules, new SaveContractApprovalRuleRequest("A kuralı", MinGrossTotal: 1m));
        var delegation = await PostAsync<ContractApprovalDelegationDto>(adminA, Delegations, new SaveContractApprovalDelegationRequest(a.ApproverA.Id, a.Delegate.Id));
        var contract = await SubmittedAsync(adminA, a.PartyId);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Rules}/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Delegations}/{delegation.Id}")).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<ContractApprovalRuleDto>>(Rules))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Contracts}/{contract.Id}/approval")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await approverB.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(contract.Version))).StatusCode);
        Assert.Empty((await approverB.GetFromJsonAsync<List<ContractApprovalInboxItemDto>>("/api/contracts/approvals/inbox?mine=false"))!);

        // B'nin kuralı A'nın sözleşmesini etkilemez: A'da hâlâ yalnızca A'nın kuralı eşleşmiş durumdadır.
        await PostAsync<ContractApprovalRuleDto>(adminB, Rules, new SaveContractApprovalRuleRequest("B kuralı", MinGrossTotal: 1m));
        var other = await SubmittedAsync(adminA, a.PartyId);
        Assert.Equal(["A kuralı"], (await adminA.GetFromJsonAsync<ContractApprovalDto>($"{Contracts}/{other.Id}/approval"))!.Rules.Select(r => r.Name));
    }

    [Fact]
    public async Task Rule_management_and_decisions_need_their_own_permissions_and_the_submitter_cannot_approve()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var writer = await LoginAsync(s.Writer);
        using var a = await LoginAsync(s.ApproverA);
        using var reader = await LoginAsync(s.Reader);

        // Kural ve vekâlet yönetimi yalnızca onay yönetimi izniyle; onaylayıcı ve yazar yönetemez, kuralları göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync(Rules, new SaveContractApprovalRuleRequest("Yetkisiz", MinGrossTotal: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(Rules)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Delegations, new SaveContractApprovalDelegationRequest(s.ApproverA.Id, s.Delegate.Id))).StatusCode);

        // Yazma izni olan hazırlayıcı onaylayamaz; onay izni olmayan okuyucu karar eylemlerini göremez/yapamaz.
        var contract = await ReadyAsync(admin, s.PartyId, nonStandard: false);
        var submitted = await RunAsync(writer, contract.Id, "submit-for-approval", contract.Version);
        var writerActions = (await writer.GetFromJsonAsync<List<ContractActionDto>>($"{Contracts}/{contract.Id}/actions"))!;
        Assert.DoesNotContain(writerActions, x => x.Action is "approve" or "reject-approval" or "return-for-changes");
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"{Contracts}/{contract.Id}/approve", new ContractTransitionRequest(submitted.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Contracts}/{contract.Id}/return-for-changes", new ContractTransitionRequest(submitted.Version, "x"))).StatusCode);

        // Hazırlayan aynı zamanda onay izni taşısa bile kendi gönderdiğini onaylayamaz (dört göz), dönüş ve ret için de.
        var withdrawn = await RunAsync(writer, contract.Id, "withdraw-approval", submitted.Version);
        var selfSubmitted = await RunAsync(admin, contract.Id, "submit-for-approval", withdrawn.Version);

        foreach (var action in new[] { "approve", "reject-approval", "return-for-changes" })
        {
            var response = await admin.PostAsJsonAsync($"{Contracts}/{contract.Id}/{action}", new ContractTransitionRequest(selfSubmitted.Version, "neden"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }

    // ---- yardımcılar

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var approverA = await factory.CreateUserAsync(tenant);
        var approverB = await factory.CreateUserAsync(tenant);
        var delegateUser = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsWrite, FinancialsRead], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, FinancialsRead, ContractsApprove], Scope.All, approverA, approverB, delegateUser);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        return new Setup(tenant, admin, writer, approverA, approverB, delegateUser, reader, party.Id);
    }

    /// <summary>İki kalemli (2.400 TRY) sözleşmeyi yayıma hazır hâle getirir; <paramref name="nonStandard"/> standart dışı madde işaretler.</summary>
    private static async Task<ContractDto> ReadyAsync(HttpClient client, Guid partyId, bool nonStandard)
    {
        var contract = await PostAsync<ContractDto>(client, Contracts, new SaveContractRequest(
            "Yıllık bakım", partyId, StartDate: new DateOnly(2026, 11, 1), EndDate: new DateOnly(2027, 10, 31), HasNonStandardTerms: nonStandard));
        await PostAsync<ContractLineDto>(client, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kurulum", Quantity: 2m, UnitPrice: 1000m, TaxRatePercent: 20m));
        contract = (await client.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        contract = await RunAsync(client, contract.Id, "send-to-review", contract.Version);
        return await RunAsync(client, contract.Id, "mark-ready", contract.Version);
    }

    private static async Task<ContractDto> SubmittedAsync(HttpClient client, Guid partyId)
    {
        var ready = await ReadyAsync(client, partyId, nonStandard: false);
        return await RunAsync(client, ready.Id, "submit-for-approval", ready.Version);
    }

    private static async Task<ContractDto> RunAsync(HttpClient client, Guid id, string action, long version, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"{Contracts}/{id}/{action}", new ContractTransitionRequest(version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
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

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
