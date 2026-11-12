using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y9b: düzenlenebilir listeler. Firma değerleri yeniden adlandırır, sıralar, pasife alır ve ekler; kod kategoriye bakar
/// (açık fırsat süzgeci, devir). Pasif değer yeni kayıtta seçilemez, onu taşıyan kayıt bozulmaz. Listeler firmaya aittir.
/// </summary>
public class ChoiceListTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, ActivitiesRead, ActivitiesWrite];

    [Fact]
    public async Task Stage_list_is_edited_by_tenant_and_categories_drive_open_filter_and_history()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        var lists = (await adminClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/customization/lists"))!;
        // Core'un belge türü listesi (ADR-0038 K3) her firmada vardır ve önce gelir; ardından CRM listeleri.
        Assert.Equal(
            ["core.credential-type", CrmLists.OpportunityStage, CrmLists.OpportunityType, CrmLists.OpportunityLossReason, CrmLists.CompanyStatus, CrmLists.CompanySource, CrmLists.ActivityType, CrmLeadLists.Source, CrmLeadLists.DisqualificationReason],
            lists.Select(l => l.Key));
        var stages = lists.Single(l => l.Key == CrmLists.OpportunityStage);
        Assert.False(stages.Customized);
        Assert.Equal(["Lead", "Qualified", "Proposal", "Negotiation", "Won", "Lost"], stages.Values.Select(v => v.Key));
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync(
            $"/api/customization/lists/{CrmLists.OpportunityStage}", new SaveChoiceListRequest([]))).StatusCode);

        var company = await PostAsync<CompanyDto>(repClient, "/api/crm/companies", new SaveCompanyRequest("Huni A.Ş."));
        var qualified = await PostAsync<OpportunityDto>(repClient, "/api/crm/opportunities",
            new SaveOpportunityRequest("Eski nitelikli", company.Id, Stage: OpportunityStage.Qualified));
        var lead = await PostAsync<OpportunityDto>(repClient, "/api/crm/opportunities", new SaveOpportunityRequest("Yeni aday", company.Id));

        // "Aday" → "İlk temas", araya "Demo yapıldı" (açık), "Nitelikli" pasif, sona "Ertelendi" (kaybedildi).
        var saved = await adminClient.PutAsJsonAsync($"/api/customization/lists/{CrmLists.OpportunityStage}", new SaveChoiceListRequest(
        [
            new("Lead", "İlk temas"), new(null, "Demo yapıldı", CrmLists.StageCategories.Open), new("Qualified", "Nitelikli", Active: false),
            new("Proposal", "Teklif"), new("Negotiation", "Pazarlık"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"),
            new(null, "Ertelendi", CrmLists.StageCategories.Lost),
        ]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var edited = (await saved.Content.ReadFromJsonAsync<ChoiceListDto>())!;
        Assert.True(edited.Customized);
        Assert.Equal(
            ["Lead:İlk temas", "demo_yapildi:Demo yapıldı", "Qualified:Nitelikli", "Proposal:Teklif", "Negotiation:Pazarlık", "Won:Kazanıldı",
             "Lost:Kaybedildi", "ertelendi:Ertelendi"],
            edited.Values.Select(v => $"{v.Key}:{v.Label}"));

        // Her üye listeyi formlar için görür.
        var visible = (await repClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!;
        Assert.Equal("İlk temas", visible.Single(l => l.Key == CrmLists.OpportunityStage).Values[0].Label);

        // Pasif değer yeni kayıtta ve başka kayda taşınırken seçilemez; onu taşıyan kayıt düzenlenebilir.
        var inactive = await repClient.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Yeni nitelikli", company.Id, Stage: OpportunityStage.Qualified));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactive.StatusCode);
        Assert.Contains("stage", await inactive.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repClient.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Bilinmeyen", company.Id, Stage: "Uydurma"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await repClient.PutAsJsonAsync($"/api/crm/opportunities/{qualified.Id}",
            new SaveOpportunityRequest("Eski nitelikli (güncel)", company.Id, Stage: OpportunityStage.Qualified, Version: qualified.Version))).StatusCode);

        // Aşama değişimi eylem uç noktasıyla yapılır (ADR-0017); genel PUT aşamayı değiştirmez.
        var moved = await repClient.PostAsJsonAsync($"/api/crm/opportunities/{lead.Id}/stage", new ChangeOpportunityStageRequest(lead.Version, "demo_yapildi"));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var postponed = await PostAsync<OpportunityDto>(repClient, "/api/crm/opportunities", new SaveOpportunityRequest("Ertelenen", company.Id));
        Assert.Equal(HttpStatusCode.OK, (await repClient.PostAsJsonAsync($"/api/crm/opportunities/{postponed.Id}/lose", new LoseOpportunityRequestDto(postponed.Version, "Timing"))).StatusCode);

        // Açık fırsat: kategorisi "Açık" olan aşamalar; firmanın eklediği "Ertelendi" (kaybedildi) kapalıdır.
        var open = (await repClient.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?companyId={company.Id}&open=true"))!;
        Assert.Equal(["Eski nitelikli (güncel)", "Yeni aday"], open.Items.Select(o => o.Title).Order());
        var closed = (await repClient.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?companyId={company.Id}&open=false"))!;
        Assert.Equal(["Ertelenen"], closed.Items.Select(o => o.Title));

        // Geçmişte değerler firmanın adlarıyla görünür.
        var history = (await repClient.GetFromJsonAsync<AuditPageResponse>($"/api/records/crm.opportunity/{lead.Id}/history"))!;
        var change = history.Items[0].Changes.Single(c => c.Field == "stage");
        Assert.Equal(("İlk temas", "Demo yapıldı"), (change.Old, change.New));

        // Liste değişikliği denetim kaydına "Liste" olarak yazılır; başka firma varsayılanı görür.
        var audit = (await adminClient.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.choice-list"))!;
        Assert.Contains(audit.Items, i => i.RecordTitle == "Fırsat aşamaları");
        var (_, otherAdmin, _) = await SetUpAsync();
        using var otherClient = await LoginAsync(otherAdmin);
        var other = (await otherClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!;
        Assert.Equal("Aday", other.Single(l => l.Key == CrmLists.OpportunityStage).Values[0].Label);
        Assert.False(other.Single(l => l.Key == CrmLists.OpportunityStage).Customized);
    }

    [Fact]
    public async Task Edits_keep_every_category_selectable_and_built_in_values_stable()
    {
        var (_, admin, _) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var url = $"/api/customization/lists/{CrmLists.OpportunityStage}";

        // Son "Kazanıldı" değeri pasife alınamaz; yeni değer kategori ister; hazır değerin kategorisi değişmez.
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(url, new SaveChoiceListRequest(
            [new("Lead", "Aday"), new("Won", "Kazanıldı", Active: false), new("Lost", "Kaybedildi")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(url, new SaveChoiceListRequest(
            [new("Lead", "Aday"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"), new(null, "Kategorisiz")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(url, new SaveChoiceListRequest(
            [new("Lead", "Aday", CrmLists.StageCategories.Won), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PutAsJsonAsync(url, new SaveChoiceListRequest(
            [new("Lead", "Aday"), new("Won", "aday"), new("Lost", "Kaybedildi")]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PutAsJsonAsync(
            "/api/customization/lists/crm.yok", new SaveChoiceListRequest([new(null, "Bir")]))).StatusCode);

        // Verilmeyen değer silinmez, pasif olarak sona eklenir.
        var response = await adminClient.PutAsJsonAsync(url, new SaveChoiceListRequest(
            [new("Won", "Kazanıldı"), new("Lead", "Aday"), new("Lost", "Kaybedildi")]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<ChoiceListDto>())!;
        Assert.Equal(
            ["Won:True", "Lead:True", "Lost:True", "Qualified:False", "Proposal:False", "Negotiation:False"],
            list.Values.Select(v => $"{v.Key}:{v.Active}"));
        Assert.All(list.Values, v => Assert.True(v.BuiltIn));
    }

    [Fact]
    public async Task Missing_required_value_takes_the_tenant_default()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        var created = await repClient.PostAsJsonAsync("/api/records/crm.activity", new RecordWriteRequest(new JsonObject { ["subject"] = "Ara" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(ActivityType.Task, (await created.Content.ReadFromJsonAsync<RecordResponse>())!.Values["type"]!.GetValue<string>());

        // Varsayılan "Görev" pasifse listenin ilk etkin değeri atanır.
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync($"/api/customization/lists/{CrmLists.ActivityType}", new SaveChoiceListRequest(
            [new("Meeting", "Toplantı"), new("Call", "Arama"), new("Email", "E-posta"), new("Visit", "Ziyaret"), new("Note", "Not"), new("Task", "Görev", Active: false)]))).StatusCode);
        var lists = (await repClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!;
        Assert.Equal(ActivityType.Meeting, lists.Single(l => l.Key == CrmLists.ActivityType).Default);
        var second = await repClient.PostAsJsonAsync("/api/records/crm.activity", new RecordWriteRequest(new JsonObject { ["subject"] = "Görüş" }));
        Assert.Equal(ActivityType.Meeting, (await second.Content.ReadFromJsonAsync<RecordResponse>())!.Values["type"]!.GetValue<string>());

        // Tipli uç nokta da tip verilmezse varsayılanı atar ("Görev"i varsaymaz); güncellemede tip verilmezse değişmez.
        var typed = await PostAsync<ActivityDto>(repClient, "/api/crm/activities", new SaveActivityRequest("Toplantı ayarla"));
        Assert.Equal(ActivityType.Meeting, typed.Type);
        var call = await PostAsync<ActivityDto>(repClient, "/api/crm/activities", new SaveActivityRequest("Geri ara", ActivityType.Call));
        var updated = await repClient.PutAsJsonAsync($"/api/crm/activities/{call.Id}", new SaveActivityRequest("Geri ara (yarın)", Version: call.Version));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(ActivityType.Call, (await updated.Content.ReadFromJsonAsync<ActivityDto>())!.Type);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repClient.PostAsJsonAsync(
            "/api/crm/activities", new SaveActivityRequest("Görev aç", ActivityType.Task))).StatusCode);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Rep)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, SalesAccess, rep);
        return (tenant, admin, rep);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
