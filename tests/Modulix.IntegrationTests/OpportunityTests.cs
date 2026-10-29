using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Fırsat yönetimi (ADR-0017): yaşam döngüsü, aşama/durum tutarlılığı, aşama koşulları, kapanış ve yeniden açma, tarihçe, kişiler ve
/// rakipler, kapsam ve tenant izolasyonu, eşzamanlılık, rapor ve dışa aktarma. Yazımlar kayıt boru hattından geçer.
/// </summary>
public class OpportunityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead,
        ActivitiesRead, ActivitiesWrite, OpportunitiesChangeStage, OpportunitiesClose, OpportunitiesExport,
        LeadsRead, LeadsCreate, LeadsUpdate, LeadsQualify, LeadsConvert,
    ];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task New_opportunity_gets_number_open_status_default_stage_probability_and_history()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);

        var first = await OpportunityAsync(client, company.Id, "Filo bakım sözleşmesi");
        var second = await OpportunityAsync(client, company.Id, "İkinci fırsat");

        Assert.StartsWith("OP-", first.OpportunityNumber);
        Assert.NotEqual(first.OpportunityNumber, second.OpportunityNumber);
        Assert.Equal(OpportunityStatus.Open, first.Status);
        Assert.Equal(OpportunityStage.Lead, first.Stage);
        Assert.Equal(10, first.Probability); // aşama varsayılanı (ayar)
        Assert.False(first.ProbabilityManual);
        Assert.Equal(OpportunityBudgetStatus.Unknown, first.BudgetStatus);
        Assert.Null(first.Amount); // bilinmeyen tutar sıfır değildir
        Assert.Equal(1, first.ClosureCycle);

        var history = await HistoryAsync(client, first.Id);
        var created = Assert.Single(history);
        Assert.Equal("Created", created.Kind);
        Assert.Equal(OpportunityStage.Lead, created.ToStage);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(2, await db.AuditLog.CountAsync(a => a.EntityKey == CrmModule.Entities.Opportunity && a.Action == AuditAction.Created));
    }

    [Fact]
    public async Task Generic_endpoints_cannot_change_stage_status_or_closing_fields()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Korumalı alanlar");

        // Tipli PUT aşamayı ve kapanış alanlarını yazamaz (değer sessizce yok sayılır).
        var put = await client.PutAsJsonAsync($"/api/crm/opportunities/{opp.Id}",
            new SaveOpportunityRequest("Korumalı alanlar", company.Id, Stage: "Negotiation", Version: opp.Version, LostReason: "x"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal(OpportunityStage.Lead, updated.Stage);
        Assert.Null(updated.LostReason);

        // Genel kayıt ucu: durum, kapanış ve numara alanları yok sayılır.
        var generic = await client.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Opportunity}/{opp.Id}", new RecordWriteRequest(new JsonObject
        {
            ["status"] = "Won", ["stage"] = "Won", ["actualCloseDate"] = Today.ToString("yyyy-MM-dd"), ["opportunityNumber"] = "OP-HACK", ["wonAmount"] = 1m,
        }, updated.Version));
        Assert.Equal(HttpStatusCode.OK, generic.StatusCode);
        var read = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opp.Id}"))!;
        Assert.Equal(OpportunityStatus.Open, read.Status);
        Assert.Equal(OpportunityStage.Lead, read.Stage);
        Assert.Null(read.ActualCloseDate);
        Assert.Equal(opp.OpportunityNumber, read.OpportunityNumber);

        // Yeni fırsat kapanmış bir aşamada açılamaz.
        var closedStart = await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Doğrudan kazanıldı", company.Id, Stage: "Won"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, closedStart.StatusCode);
    }

    [Fact]
    public async Task Stage_change_requires_stage_fields_and_the_same_rules_apply_to_every_caller()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Teklif aşaması");

        // Varsayılan ayar: Teklif aşamasına geçmek için tutar ve tahmini kapanış dolu olmalı.
        var blocked = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Proposal"));
        var problem = await ProblemAsync(blocked);
        Assert.Contains("amount", problem.Errors.Keys);
        Assert.Contains("expectedCloseDate", problem.Errors.Keys);
        Assert.Equal(OpportunityStage.Lead, (await GetAsync(client, opp.Id)).Stage);

        // Kanban da aynı uç noktayı kullanır; kapanış aşamasına eylem dışı yoldan geçilemez.
        var toWon = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Won"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toWon.StatusCode);
        var unknown = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Yok"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var filled = await PutAsync(client, opp, amount: 50_000m, close: Today.AddDays(30));
        var moved = await StageAsync(client, filled, "Proposal", "Teklif gönderilecek");
        Assert.Equal("Proposal", moved.Opportunity.Stage);
        Assert.Equal(50, moved.Opportunity.Probability); // elle girilmedi: aşama varsayılanı
        Assert.False(moved.Opportunity.ProbabilityManual);

        var history = await HistoryAsync(client, opp.Id);
        var step = history.Last();
        Assert.Equal("Stage", step.Kind);
        Assert.Equal(OpportunityStage.Lead, step.FromStage);
        Assert.Equal("Proposal", step.ToStage);
        Assert.Equal("Teklif gönderilecek", step.Reason);
        Assert.NotNull(step.ChangedBy);

        // Aynı aşamaya yeniden geçiş (çift tıklama) tarihçeyi çoğaltmaz.
        var again = await StageAsync(client, moved.Opportunity, "Proposal", null);
        Assert.Equal(moved.Opportunity.Version, again.Opportunity.Version);
        Assert.Equal(history.Count, (await HistoryAsync(client, opp.Id)).Count);
    }

    [Fact]
    public async Task Manual_probability_survives_stage_changes_and_closing_sets_it_explicitly()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Olasılık");

        var manual = await PutAsync(client, opp, amount: 1_000m, close: Today.AddDays(10), probability: 33);
        Assert.True(manual.ProbabilityManual);
        var moved = await StageAsync(client, manual, "Qualified", null);
        Assert.Equal(33, moved.Opportunity.Probability); // elle girilen ezilmez

        var won = await WinAsync(client, moved.Opportunity);
        Assert.Equal(100, won.Opportunity.Probability);
        Assert.False(won.Opportunity.ProbabilityManual);
    }

    [Fact]
    public async Task Winning_records_actual_values_and_a_closed_opportunity_cannot_change_stage()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Kazanılacak"), amount: 80_000m, close: Today.AddDays(5));

        var win = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win",
            new WinOpportunityRequestDto(opp.Version, ActualCloseDate: Today.AddDays(1)));
        Assert.Contains("actualCloseDate", (await ProblemAsync(win)).Errors.Keys); // gelecekte kapanış olmaz

        var result = await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win",
            new WinOpportunityRequestDto(opp.Version, WonAmount: 75_000m, WonCurrency: "TRY", Reason: "Fiyat avantajı", HandoverNote: "Kurulum ekibine devredilecek")));
        var won = result.Opportunity;
        Assert.Equal(OpportunityStatus.Won, won.Status);
        Assert.Equal(OpportunityStage.Won, won.Stage);
        Assert.Equal(Today, won.ActualCloseDate);
        Assert.Equal(75_000m, won.WonAmount); // gerçekleşen tutar tahminden ayrı
        Assert.Equal(80_000m, won.Amount);
        Assert.Equal("Kurulum ekibine devredilecek", won.HandoverNote);

        var stage = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(won.Version, "Qualified"));
        Assert.Contains("status", (await ProblemAsync(stage)).Errors.Keys);
        var cancel = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/cancel", new CancelOpportunityRequest(won.Version, "vazgeçildi"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancel.StatusCode);
    }

    [Fact]
    public async Task Winning_without_any_known_amount_is_rejected()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Tutarsız");

        var problem = await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version)));
        Assert.Contains("wonAmount", problem.Errors.Keys);
        Assert.Equal(OpportunityStatus.Open, (await GetAsync(client, opp.Id)).Status);
    }

    [Fact]
    public async Task Losing_requires_a_structured_reason_other_needs_a_note_and_open_activities_are_kept()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Kaybedilecek"), amount: 10_000m, close: Today.AddDays(9));
        var activity = await ReadAsync<ActivityDto>(await client.PostAsJsonAsync("/api/crm/activities",
            new SaveActivityRequest("Açık görüşme", "Task", DueAt: DateTimeOffset.UtcNow.AddDays(2), OpportunityId: opp.Id, CompanyId: company.Id)), HttpStatusCode.Created);

        Assert.Contains("lossReason", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose", new LoseOpportunityRequestDto(opp.Version, "")))).Errors.Keys);
        Assert.Contains("lossReason", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose", new LoseOpportunityRequestDto(opp.Version, "Yok")))).Errors.Keys);
        Assert.Contains("lostReason", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose", new LoseOpportunityRequestDto(opp.Version, "Other")))).Errors.Keys);

        // Vazgeçme rakibe kayıp değildir: kazanan rakip belirtilemez.
        var competitor = await ReadAsync<OpportunityCompetitorDto>(await client.PostAsJsonAsync("/api/crm/opportunity-competitors",
            new SaveOpportunityCompetitorRequest(opp.Id, "Rakip A.Ş.", Strengths: "Fiyat", AlternativeAmount: 9_000m, AlternativeCurrency: "TRY")), HttpStatusCode.Created);
        var notCompetitor = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose",
            new LoseOpportunityRequestDto(opp.Version, "NoDecision", WinningCompetitorId: competitor.Id));
        Assert.Contains("winningCompetitorId", (await ProblemAsync(notCompetitor)).Errors.Keys);

        var result = await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose",
            new LoseOpportunityRequestDto(opp.Version, "Competitor", Note: "Fiyatta geride kaldık", WinningCompetitorId: competitor.Id,
                RecontactDate: Today.AddDays(90), CreateFollowUp: true)));
        Assert.Equal(OpportunityStatus.Lost, result.Opportunity.Status);
        Assert.Equal("Competitor", result.Opportunity.LossReason);
        Assert.Equal(competitor.Id, result.Opportunity.WinningCompetitorId);
        Assert.Equal(Today.AddDays(90), result.Opportunity.RecontactDate);
        Assert.Equal(0, result.Opportunity.Probability);
        Assert.NotNull(result.FollowUpActivityId);

        // Açık aktivite silinmedi ve sessizce kapatılmadı; sayısı bildirildi (kaybedilen fırsatta 2: eldeki + takip).
        Assert.Equal(2, result.OpenActivities);
        var kept = (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{activity.Id}"))!;
        Assert.Null(kept.CompletedAt);

        var lostBy = (await HistoryAsync(client, opp.Id)).Last();
        Assert.Equal(OpportunityStatus.Lost, lostBy.ToStatus);
        Assert.Contains("Competitor", lostBy.Details);
    }

    [Fact]
    public async Task Cancelling_keeps_the_open_stage_and_is_not_counted_as_lost()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "İptal"), amount: 5_000m, close: Today.AddDays(4));
        var moved = (await StageAsync(client, opp, "Qualified", null)).Opportunity;

        Assert.Contains("statusReason", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/cancel", new CancelOpportunityRequest(moved.Version, " ")))).Errors.Keys);
        var cancelled = (await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/cancel",
            new CancelOpportunityRequest(moved.Version, "Müşteri projeyi iptal etti")))).Opportunity;
        Assert.Equal(OpportunityStatus.Cancelled, cancelled.Status);
        Assert.Equal("Qualified", cancelled.Stage); // kaybedildi aşamasına taşınmaz
        Assert.Equal(0, cancelled.Probability);
        Assert.Null(cancelled.LossReason);
    }

    [Fact]
    public async Task Hold_needs_reason_and_future_review_date_and_blocks_stage_changes_until_resumed()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Bekletilen");

        Assert.Contains("statusReason", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/hold", new HoldOpportunityRequest(opp.Version, "", Today.AddDays(10))))).Errors.Keys);
        Assert.Contains("holdReviewDate", (await ProblemAsync(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/hold", new HoldOpportunityRequest(opp.Version, "Bütçe onayı bekleniyor", Today)))).Errors.Keys);

        var held = (await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/hold",
            new HoldOpportunityRequest(opp.Version, "Bütçe onayı bekleniyor", Today.AddDays(30))))).Opportunity;
        Assert.Equal(OpportunityStatus.OnHold, held.Status);
        Assert.Equal(Today.AddDays(30), held.HoldReviewDate);

        // Bekleme kapanış değildir: kapatılamaz, aşaması değişmez; açık listesinde sayılmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(held.Version, WonAmount: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(held.Version, "Qualified"))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?open=true&number={opp.OpportunityNumber}"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?live=true&number={opp.OpportunityNumber}"))!.Items);

        var resumed = (await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/resume", new ResumeOpportunityRequest(held.Version)))).Opportunity;
        Assert.Equal(OpportunityStatus.Open, resumed.Status);
        Assert.Null(resumed.HoldReviewDate);
        Assert.Null(resumed.StatusReason);
    }

    [Fact]
    public async Task Reopening_needs_its_own_permission_and_reason_keeps_old_closure_in_history_and_starts_a_new_cycle()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Yeniden açılacak"), amount: 20_000m, close: Today.AddDays(3));
        var won = (await WinAsync(client, opp)).Opportunity;

        // Satış temsilcisi (kapatma izni var, yeniden açma yok) yeniden açamaz.
        var reopenBody = new ReopenOpportunityRequestDto(won.Version, "Müşteri döndü", "Qualified", Today.AddDays(20));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/reopen", reopenBody)).StatusCode);

        var tenant = await TenantAsync(tenantId);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenantId, [OpportunitiesRead, OpportunitiesReopen, OpportunityAmountsRead], manager);
        using var managerClient = await LoginAsync(manager.Email!);

        Assert.Contains("statusReason", (await ProblemAsync(await managerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/reopen", reopenBody with { Reason = " " }))).Errors.Keys);
        Assert.Contains("stage", (await ProblemAsync(await managerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/reopen", reopenBody with { Stage = "Won" }))).Errors.Keys);
        Assert.Contains("expectedCloseDate", (await ProblemAsync(await managerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/reopen", reopenBody with { ExpectedCloseDate = Today.AddDays(-1) }))).Errors.Keys);

        var reopened = (await ReadAsync<OpportunityActionResultDto>(await managerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/reopen", reopenBody))).Opportunity;
        Assert.Equal(OpportunityStatus.Open, reopened.Status);
        Assert.Equal("Qualified", reopened.Stage);
        Assert.Equal(2, reopened.ClosureCycle);
        Assert.Null(reopened.ActualCloseDate);
        Assert.Null(reopened.WonAmount);
        Assert.Equal(25, reopened.Probability); // 100 taşınmaz: aşamanın varsayılanı

        var history = await HistoryAsync(managerClient, opp.Id);
        var closure = Assert.Single(history, h => h.ToStatus == OpportunityStatus.Won);
        Assert.Equal(1, closure.Cycle);
        Assert.Contains("20000", closure.Details); // eski kapanış bilgileri tarihçede kaldı
        var reopen = history.Last();
        Assert.Equal(OpportunityStatus.Won, reopen.FromStatus);
        Assert.Equal(OpportunityStatus.Open, reopen.ToStatus);
        Assert.Equal("Müşteri döndü", reopen.Reason);
        Assert.Equal(2, reopen.Cycle);

        // Aynı fırsat ikinci kez kazanılınca tek fırsat olarak sayılır; rapor mükerrer saymaz.
        var again = (await WinAsync(client, reopened)).Opportunity;
        var report = await ReportAsync(client);
        Assert.Equal(1, report.WonCount);
        Assert.Equal(again.ClosureCycle, 2);
    }

    [Fact]
    public async Task Stale_version_on_an_action_is_a_conflict_and_changes_nothing()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Eşzamanlı"), amount: 3_000m, close: Today.AddDays(8));
        var moved = (await StageAsync(client, opp, "Qualified", null)).Opportunity;

        var stale = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Proposal"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleWin = await client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version));
        Assert.Equal(HttpStatusCode.Conflict, staleWin.StatusCode);
        var current = await GetAsync(client, opp.Id);
        Assert.Equal("Qualified", current.Stage);
        Assert.Equal(moved.Version, current.Version);

        // Eski sürümle genel güncelleme de sessizce ezmez.
        var oldPut = await client.PutAsJsonAsync($"/api/crm/opportunities/{opp.Id}", new SaveOpportunityRequest("Ezme", company.Id, Version: opp.Version));
        Assert.Equal(HttpStatusCode.Conflict, oldPut.StatusCode);
    }

    [Fact]
    public async Task Close_date_changes_are_counted_and_kept_in_history()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Ertelenen"), amount: 1m, close: Today.AddDays(10));
        var postponed = await PutAsync(client, opp, amount: 1m, close: Today.AddDays(40));

        Assert.Equal(1, postponed.CloseDateChangeCount); // ilk giriş erteleme değildir, değişim sayılır
        var change = (await HistoryAsync(client, opp.Id)).Last(h => h.Kind == "CloseDate");
        Assert.Equal(Today.AddDays(10), change.FromCloseDate);
        Assert.Equal(Today.AddDays(40), change.ToCloseDate);
        var page = await client.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities?closeDatePostponed=true");
        Assert.Contains(page!.Items, o => o.Id == opp.Id);
    }

    [Fact]
    public async Task Contacts_carry_their_roles_on_the_opportunity_relation_and_competitors_stay_with_the_opportunity()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "Kişiler");
        var person = await ReadAsync<ContactDto>(await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest("Ayşe", "Karar", CompanyId: company.Id)), HttpStatusCode.Created);

        var link = await ReadAsync<OpportunityContactDto>(await client.PostAsJsonAsync("/api/crm/opportunity-contacts",
            new SaveOpportunityContactRequest(opp.Id, person.Id, ["DecisionMaker", "BudgetOwner"], IsPrimary: true, Note: "Nihai karar onda")), HttpStatusCode.Created);
        Assert.Equal(["DecisionMaker", "BudgetOwner"], link.Roles);
        Assert.Equal("Ayşe Karar", link.ContactName);

        // Aynı kişi ikinci kez bağlanamaz; ikinci ana irtibat da olamaz; geçersiz rol reddedilir.
        Assert.Contains("contactId", (await ProblemAsync(await client.PostAsJsonAsync("/api/crm/opportunity-contacts", new SaveOpportunityContactRequest(opp.Id, person.Id)))).Errors.Keys);
        var other = await ReadAsync<ContactDto>(await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest("Veli", "Teknik")), HttpStatusCode.Created);
        Assert.Contains("isPrimary", (await ProblemAsync(await client.PostAsJsonAsync("/api/crm/opportunity-contacts", new SaveOpportunityContactRequest(opp.Id, other.Id, IsPrimary: true)))).Errors.Keys);
        Assert.Contains("roles", (await ProblemAsync(await client.PostAsJsonAsync("/api/crm/opportunity-contacts", new SaveOpportunityContactRequest(opp.Id, other.Id, ["Yok"])))).Errors.Keys);

        // Rol kişi kaydına sızmaz: kişi kaydında rol alanı yoktur; ilişki fırsata göre süzülür.
        var list = (await client.GetFromJsonAsync<CrmPage<OpportunityContactDto>>($"/api/crm/opportunity-contacts?opportunityId={opp.Id}"))!;
        Assert.Single(list.Items);

        // Rakip tutarı için para birimi gerekir; fırsatla birlikte silinir.
        Assert.Contains("alternativeCurrency", (await ProblemAsync(await client.PostAsJsonAsync("/api/crm/opportunity-competitors",
            new SaveOpportunityCompetitorRequest(opp.Id, "Rakip", AlternativeAmount: 5m)))).Errors.Keys);
        await ReadAsync<OpportunityCompetitorDto>(await client.PostAsJsonAsync("/api/crm/opportunity-competitors", new SaveOpportunityCompetitorRequest(opp.Id, "Rakip 2")), HttpStatusCode.Created);
        var current = await GetAsync(client, opp.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/crm/opportunities/{opp.Id}?version={current.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/crm/opportunities/{opp.Id}")).StatusCode);
    }

    [Fact]
    public async Task Other_tenant_and_out_of_scope_users_cannot_read_or_act_on_an_opportunity()
    {
        var (a, tenantId, _) = await ClientAsync();
        var (b, _, _) = await ClientAsync();
        using var _a = a;
        using var _b = b;
        var company = await CompanyAsync(a);
        var opp = await PutAsync(a, await OpportunityAsync(a, company.Id, "Yalnızca A"), amount: 100m, close: Today.AddDays(2));

        // Başka tenant: her yüzey 404 (varlığı belli edilmez).
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/crm/opportunities/{opp.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/crm/opportunities/{opp.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version, WonAmount: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Qualified"))).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities"))!.Items);
        var link = await ProblemAsync(await b.PostAsJsonAsync("/api/crm/opportunity-contacts", new SaveOpportunityContactRequest(opp.Id, Guid.NewGuid())));
        Assert.Contains("opportunityId", link.Errors.Keys);

        // Aynı tenant, yalnızca kendi kayıtlarını gören temsilci: başkasının fırsatı yok sayılır; eylem izni olsa bile 404.
        var tenant = await TenantAsync(tenantId);
        var rep = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenantId, SalesPermissions, RecordScope.Own, rep);
        using var repClient = await LoginAsync(rep.Email!);
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.GetAsync($"/api/crm/opportunities/{opp.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.GetAsync($"/api/crm/opportunities/{opp.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version, WonAmount: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Qualified"))).StatusCode);
        Assert.Empty((await repClient.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities"))!.Items);
        Assert.Equal(0, (await ReportAsync(repClient)).OpenCount);
        Assert.Empty((await repClient.GetFromJsonAsync<CrmPage<OpportunityContactDto>>($"/api/crm/opportunity-contacts?opportunityId={opp.Id}"))!.Items);
    }

    [Fact]
    public async Task Actions_need_their_own_permission_after_the_record_is_found()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "İzinler"), amount: 10m, close: Today.AddDays(2));

        var tenant = await TenantAsync(tenantId);
        var viewer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenantId, [OpportunitiesRead, OpportunityAmountsRead], viewer);
        using var viewerClient = await LoginAsync(viewer.Email!);

        Assert.Equal(HttpStatusCode.OK, (await viewerClient.GetAsync($"/api/crm/opportunities/{opp.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/stage", new ChangeOpportunityStageRequest(opp.Version, "Qualified"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version, WonAmount: 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/hold", new HoldOpportunityRequest(opp.Version, "x", Today.AddDays(5)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync("/api/crm/opportunities/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync("/api/crm/opportunity-competitors", new SaveOpportunityCompetitorRequest(opp.Id, "Rakip"))).StatusCode);
        Assert.Equal("Lead", (await GetAsync(client, opp.Id)).Stage);
    }

    [Fact]
    public async Task Amounts_stay_hidden_from_users_without_the_amount_permission_in_reports_history_and_export()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await PutAsync(client, await OpportunityAsync(client, company.Id, "Gizli tutar"), amount: 424_242m, close: Today.AddDays(2));
        await WinAsync(client, opp);

        var tenant = await TenantAsync(tenantId);
        var viewer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenantId, [OpportunitiesRead, OpportunitiesExport], viewer);
        using var viewerClient = await LoginAsync(viewer.Email!);

        var report = await ReportAsync(viewerClient);
        Assert.False(report.AmountsVisible);
        Assert.Equal(1, report.WonCount);
        Assert.All(report.WonTotals, t => Assert.Null(t.Amount));
        Assert.All(await HistoryAsync(viewerClient, opp.Id), h => Assert.Null(h.Details));
        var csv = await (await viewerClient.GetAsync("/api/crm/opportunities/export")).Content.ReadAsStringAsync();
        Assert.Contains(opp.OpportunityNumber!, csv);
        Assert.DoesNotContain("424242", csv);
    }

    [Fact]
    public async Task Report_keeps_currencies_apart_weights_amounts_and_defines_win_rate_without_cancelled_or_open()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);

        // Açık: 1000 TRY %50 (ağırlıklı 500), 200 USD %25 (50), 300 TRY olasılığı bilinmeyen değil %10 (30); tutarı bilinmeyen biri.
        var tryOpp = await PutAsync(client, await OpportunityAsync(client, company.Id, "TRY açık"), amount: 1_000m, close: Today.AddDays(5), probability: 50);
        var usd = await PutAsync(client, await OpportunityAsync(client, company.Id, "USD açık"), amount: 200m, close: Today.AddDays(40), probability: 25, currency: "USD");
        await PutAsync(client, await OpportunityAsync(client, company.Id, "TRY küçük"), amount: 300m, close: Today.AddDays(5), probability: 10);
        await OpportunityAsync(client, company.Id, "Tutarsız");

        // Kapalı: 1 kazanıldı, 1 kaybedildi, 1 iptal.
        var won = await PutAsync(client, await OpportunityAsync(client, company.Id, "Kazanılan"), amount: 900m, close: Today.AddDays(1));
        await WinAsync(client, won);
        var lost = await PutAsync(client, await OpportunityAsync(client, company.Id, "Kaybedilen"), amount: 100m, close: Today.AddDays(1));
        await client.PostAsJsonAsync($"/api/crm/opportunities/{lost.Id}/lose", new LoseOpportunityRequestDto(lost.Version, "Price"));
        var cancelled = await PutAsync(client, await OpportunityAsync(client, company.Id, "İptal edilen"), amount: 700m, close: Today.AddDays(1));
        await client.PostAsJsonAsync($"/api/crm/opportunities/{cancelled.Id}/cancel", new CancelOpportunityRequest(cancelled.Version, "vazgeçti"));

        var report = await ReportAsync(client);
        Assert.Equal(4, report.OpenCount);
        var tryTotal = Assert.Single(report.OpenTotals, t => t.Currency == "TRY");
        var usdTotal = Assert.Single(report.OpenTotals, t => t.Currency == "USD");
        Assert.Equal(3, tryTotal.Count);
        Assert.Equal(1_300m, tryTotal.Amount); // 1000 + 300; tutarsız fırsat toplama sıfır olarak girmez
        Assert.Equal(530m, tryTotal.Weighted); // 500 + 30
        Assert.Equal(1, tryTotal.UnknownAmountCount);
        Assert.Equal(200m, usdTotal.Amount); // para birimleri toplanmaz
        Assert.Equal(50m, usdTotal.Weighted);
        Assert.Equal(["TRY", "USD"], report.OpenTotals.Select(t => t.Currency).Order().ToArray());

        Assert.Equal(1, report.WonCount);
        Assert.Equal(1, report.LostCount);
        Assert.Equal(1, report.CancelledCount);
        Assert.Equal(0.5, report.WinRate); // 1 / (1 + 1): iptal ve açıklar paydada yok
        Assert.Equal(900m, Assert.Single(report.WonTotals).Amount);
        Assert.Equal("Price", Assert.Single(report.LossReasons).Key);
        Assert.NotEmpty(report.Definitions);
        Assert.Empty(report.Overdue); // tüm tahmini kapanışlar gelecekte
        Assert.Contains(report.NoNextAction, i => i.Title == "Tutarsız");
        Assert.Contains(report.Forecast, m => m.Totals.Any(t => t.Currency == "USD"));
        Assert.DoesNotContain(report.Forecast.SelectMany(m => m.Totals), t => t.Currency == "EUR");
        Assert.True(usd.Id != Guid.Empty && tryOpp.Id != Guid.Empty);
    }

    [Fact]
    public async Task Win_rate_is_undefined_when_nothing_was_decided()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        await OpportunityAsync(client, company.Id, "Hiç kapanmadı");
        var report = await ReportAsync(client);
        Assert.Null(report.WinRate);
        Assert.Null(report.AverageCycleDays);
        Assert.Equal(1, report.OpenCount);
    }

    [Fact]
    public async Task Export_requires_its_own_permission_and_is_audited()
    {
        var (client, tenantId, user) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opp = await OpportunityAsync(client, company.Id, "=HYPERLINK(\"x\")");

        var csv = await client.GetAsync("/api/crm/opportunities/export");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains(opp.OpportunityNumber!, text);
        Assert.Contains("'=HYPERLINK", text); // CSV enjeksiyonuna karşı kesme işareti

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Contains(await db.AuditLog.Where(a => a.Action == AuditAction.Exported).ToListAsync(),
            a => a.EntityKey == CrmModule.Entities.Opportunity && a.UserId == user.Id);
    }

    [Fact]
    public async Task Lead_conversion_creates_a_numbered_opportunity_once_with_history()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var lead = await ReadAsync<LeadDto>(await client.PostAsJsonAsync("/api/crm/leads",
            new SaveLeadRequest("Web talebi", FirstName: "Can", LastName: "Demir", CompanyName: "Dönüşüm A.Ş.", Email: "can@donusum.test",
                NeedDescription: "Kapsam netleşti", EstimatedBudget: 12_000m, Source: "Referral")), HttpStatusCode.Created);
        var qualified = await ReadAsync<LeadDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version)));

        var request = new ConvertLeadRequest(qualified.Version, CreateCompany: true, CreateContact: true);
        var first = await ReadAsync<ConvertLeadResultDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", request));
        var second = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", request); // çift tıklama
        Assert.False(second.IsSuccessStatusCode);

        var opp = await GetAsync(client, first.OpportunityId);
        Assert.StartsWith("OP-", opp.OpportunityNumber);
        Assert.Equal(lead.Id, opp.SourceLeadId);
        Assert.Equal(OpportunityStatus.Open, opp.Status);
        Assert.Single(await HistoryAsync(client, opp.Id));
        var all = await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?sourceLeadId={lead.Id}");
        Assert.Single(all!.Items);
    }

    [Fact]
    public void Shared_opportunity_keys_match_the_module()
    {
        Assert.Equal(CrmModule.Permissions.OpportunitiesChangeStage, CrmAccess.OpportunitiesChangeStage);
        Assert.Equal(CrmModule.Permissions.OpportunitiesClose, CrmAccess.OpportunitiesClose);
        Assert.Equal(CrmModule.Permissions.OpportunitiesReopen, CrmAccess.OpportunitiesReopen);
        Assert.Equal(CrmModule.Permissions.OpportunitiesExport, CrmAccess.OpportunitiesExport);
        Assert.Equal(CrmModule.Entities.OpportunityContact, CrmEntityKeys.OpportunityContact);
        Assert.Equal(CrmModule.Entities.OpportunityCompetitor, CrmEntityKeys.OpportunityCompetitor);
        Assert.Equal(CrmModule.Lists.OpportunityLossReason, CrmLists.OpportunityLossReason);
        Assert.Equal(CrmModule.Lists.OpportunityType, CrmLists.OpportunityType);
        Assert.Equal(Modulix.Modules.Crm.Domain.OpportunityStatus.All, OpportunityStatus.All);
        Assert.Equal(Modulix.Modules.Crm.Domain.OpportunityBudgetStatus.All, OpportunityBudgetStatus.All);
        Assert.Equal(CrmModule.OpportunityContactRoles, OpportunityContactRoles.All);
    }

    // ---- yardımcılar

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Modulix.Core.Platform.Tenant> tenants = new();

    private async Task<(HttpClient Client, Guid TenantId, ModulixUser User)> ClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, user);
        tenants[tenant.Id] = tenant;
        return (await LoginAsync(user.Email!), tenant.Id, user);
    }

    private Task<Modulix.Core.Platform.Tenant> TenantAsync(Guid tenantId) => Task.FromResult(tenants[tenantId]);

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client) =>
        await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Firma {Guid.NewGuid():N}"[..14])), HttpStatusCode.Created);

    private static async Task<OpportunityDto> OpportunityAsync(HttpClient client, Guid companyId, string title) =>
        await ReadAsync<OpportunityDto>(await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest(title, companyId)), HttpStatusCode.Created);

    /// <summary>Tipli PUT kaydın tamamını yazar: başlık ve firma korunur, istenen alanlar verilir.</summary>
    private static async Task<OpportunityDto> PutAsync(
        HttpClient client, OpportunityDto current, decimal? amount = null, DateOnly? close = null, int? probability = null, string currency = "TRY") =>
        await ReadAsync<OpportunityDto>(await client.PutAsJsonAsync($"/api/crm/opportunities/{current.Id}",
            new SaveOpportunityRequest(current.Title, current.CompanyId, Amount: amount, Currency: currency, ExpectedCloseDate: close,
                Probability: probability, Version: current.Version)));

    private static async Task<OpportunityDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{id}"))!;

    private static async Task<OpportunityActionResultDto> StageAsync(HttpClient client, OpportunityDto current, string stage, string? note) =>
        await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{current.Id}/stage",
            new ChangeOpportunityStageRequest(current.Version, stage, note)));

    private static async Task<OpportunityActionResultDto> WinAsync(HttpClient client, OpportunityDto current) =>
        await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{current.Id}/win", new WinOpportunityRequestDto(current.Version)));

    private static async Task<IReadOnlyList<OpportunityHistoryDto>> HistoryAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/crm/opportunities/{id}/history");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<OpportunityHistoryDto>>())!;
    }

    private static async Task<OpportunityReportDto> ReportAsync(HttpClient client)
    {
        var from = Today.AddDays(-30).ToString("yyyy-MM-dd");
        var to = Today.ToString("yyyy-MM-dd");
        return await ReadAsync<OpportunityReportDto>(await client.GetAsync($"/api/crm/opportunities/report?from={from}&to={to}"));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        Assert.True(expected is null ? response.IsSuccessStatusCode : response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
