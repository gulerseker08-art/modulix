using System.Net;
using System.Net.Http.Json;
using Modulix.Core.Contracts.Authorization;
using Modulix.Modules.Marketing;
using Modulix.Shared.Marketing;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

// ADR-0025 dilim 8: kampanya raporu (gönderim, yanıt, lead, ilk/son dokunuş, maliyet) ve müşteri kartı geçmişi. Dört test türü; CampaignRunTests'in dünya kurulumunu paylaşır.
public partial class CampaignRunTests
{
    private static readonly string[] Reporter = [CampaignsRead, ResponsesRead, RecipientsRead, ReportsRead];

    private static async Task SetCostAsync(HttpClient client, CampaignDto campaign, decimal? budget, decimal? cost, string? currency = null)
    {
        var current = (await client.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{campaign.Id}"))!;
        var response = await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}",
            new SaveCampaignRequest(current.Version, current.Code, current.Name, current.Description, current.Purpose, current.Target, current.Channel, current.RecipientRule,
                current.RecipientRole, current.MaxPerTarget, current.StartsOn, current.EndsOn, current.OwnerId, null, null, null, budget, cost, currency));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<CampaignReportDto> ReportAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<CampaignReportDto>($"/api/marketing/reports/campaigns{query}"))!;

    // ---- Mutlu yol ----

    [Fact]
    public async Task The_report_counts_sends_responses_leads_touches_and_unit_costs_honestly()
    {
        var s = await SentWorldAsync();
        await SetCostAsync(s.World.Preparer, s.World.Campaign, 1000m, 300m);
        var interested = await RegisterAsync(s.Responder, s.Accepted[0].Id, "Interested");
        await ResponseActionAsync(s.Responder, interested.Id, "convert-to-lead");
        await RegisterAsync(s.Responder, s.Accepted[1].Id, "OptOut");
        var reporter = await UserClientAsync(s.World.Tenant, Reporter);

        var row = Assert.Single((await ReportAsync(reporter)).Rows);

        // Kabul edilen 2 + sonucu belirsiz 1 = gönderildi 3 ("teslim edildi" değil); 2 hatalı ileti gönderilmiş sayılmaz.
        Assert.Equal((1, 3, 2, 1, 2), (row.Runs, row.Sent, row.Accepted, row.Unknown, row.Failed));
        Assert.Equal((2, 1, 1, 0), (row.Responses, row.Interested, row.OptOuts, row.Complaints));
        Assert.Equal((1, 1, 1), (row.Leads, row.FirstTouchLeads, row.LastTouchLeads));
        Assert.Equal((1000m, 300m, "TRY", 150m, 300m), (row.Budget, row.ActualCost, row.Currency, row.CostPerResponse, row.CostPerLead));
        Assert.Equal(s.World.Campaign.Code, row.Code);

        // Süzgeçler: kanal ve kampanya.
        Assert.Single((await ReportAsync(reporter, "?channel=Email")).Rows);
        Assert.Empty((await ReportAsync(reporter, "?channel=Sms")).Rows);
        Assert.Empty((await ReportAsync(reporter, $"?campaignId={Guid.NewGuid()}")).Rows);
    }

    [Fact]
    public async Task A_target_history_lists_messages_and_responses_without_addresses()
    {
        var s = await SentWorldAsync();
        var delivery = s.Accepted[0];
        await RegisterAsync(s.Responder, delivery.Id, "Question");
        var reporter = await UserClientAsync(s.World.Tenant, Reporter);

        var raw = await reporter.GetStringAsync($"/api/marketing/targets/{delivery.TargetId}/history");
        var history = (await reporter.GetFromJsonAsync<TargetHistoryDto>($"/api/marketing/targets/{delivery.TargetId}/history"))!;

        Assert.DoesNotContain("@", raw);
        Assert.Contains(history.Items, i => i is { Kind: "Delivery", Status: "Accepted" } && i.CampaignName == s.World.Campaign.Name);
        Assert.Contains(history.Items, i => i is { Kind: "Response", Detail: "Question" });
        Assert.Empty((await reporter.GetFromJsonAsync<TargetHistoryDto>($"/api/marketing/targets/{Guid.NewGuid()}/history"))!.Items);
    }

    // ---- Hata / doğrulama ----

    [Fact]
    public async Task Cost_values_are_validated_and_the_currency_is_normalized()
    {
        var s = await SentWorldAsync();
        var campaign = (await s.World.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{s.World.Campaign.Id}"))!;

        SaveCampaignRequest Save(decimal? budget, decimal? cost, string? currency) => new(campaign.Version, campaign.Code, campaign.Name, campaign.Description, campaign.Purpose,
            campaign.Target, campaign.Channel, campaign.RecipientRule, campaign.RecipientRole, campaign.MaxPerTarget, campaign.StartsOn, campaign.EndsOn, campaign.OwnerId, null, null, null, budget, cost, currency);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", Save(-1m, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", Save(null, 10.123m, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", Save(null, 10m, "TL1"))).StatusCode);

        var saved = await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", Save(500m, 120.5m, "usd"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var dto = (await saved.Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(("USD", 120.5m), (dto.CostCurrency, dto.ActualCost));

        // Maliyet yoksa birim maliyet bilinmez (sıfır gösterilmez).
        await SetCostAsync(s.World.Preparer, s.World.Campaign, null, null, "TRY");
        var reporter = await UserClientAsync(s.World.Tenant, Reporter);
        var row = Assert.Single((await ReportAsync(reporter)).Rows);
        Assert.Equal((null, null), (row.CostPerResponse, row.CostPerLead));
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Reports_and_histories_never_cross_tenants()
    {
        var a = await SentWorldAsync();
        var b = await SentWorldAsync();
        await RegisterAsync(a.Responder, a.Accepted[0].Id, "Interested");
        var reporterA = await UserClientAsync(a.World.Tenant, Reporter);
        var reporterB = await UserClientAsync(b.World.Tenant, Reporter);

        Assert.Equal(a.World.Campaign.Id, Assert.Single((await ReportAsync(reporterA)).Rows).CampaignId);
        var rowB = Assert.Single((await ReportAsync(reporterB)).Rows);
        Assert.Equal((b.World.Campaign.Id, 0), (rowB.CampaignId, rowB.Responses));

        Assert.Empty((await reporterB.GetFromJsonAsync<TargetHistoryDto>($"/api/marketing/targets/{a.Accepted[0].TargetId}/history"))!.Items);
    }

    // ---- Yetki ----

    [Fact]
    public async Task Reports_need_their_own_permission_and_the_history_also_needs_recipient_access_and_scope()
    {
        var s = await SentWorldAsync();
        var delivery = s.Accepted[0];
        var noReports = await UserClientAsync(s.World.Tenant, [CampaignsRead, ResponsesRead, RecipientsRead]);
        var noRecipients = await UserClientAsync(s.World.Tenant, [CampaignsRead, ReportsRead]);

        Assert.Equal(HttpStatusCode.Forbidden, (await noReports.GetAsync("/api/marketing/reports/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noReports.GetAsync($"/api/marketing/targets/{delivery.TargetId}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await noRecipients.GetAsync("/api/marketing/reports/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRecipients.GetAsync($"/api/marketing/targets/{delivery.TargetId}/history")).StatusCode);

        // Dar kapsamlı kullanıcı başkasının kampanyasını raporda görmez ve geçmişinde de yoktur.
        var scoped = await factory.CreateUserAsync(s.World.Tenant);
        await factory.CreateRoleAsync(s.World.Tenant.Id, Reporter, RecordScope.Own, scoped);
        var scopedClient = await LoginAsync(scoped);
        Assert.Empty((await ReportAsync(scopedClient)).Rows);
        Assert.Empty((await scopedClient.GetFromJsonAsync<TargetHistoryDto>($"/api/marketing/targets/{delivery.TargetId}/history"))!.Items);

        using var anonymous = host.CreateClient(Browser());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/marketing/reports/campaigns")).StatusCode);
    }
}
