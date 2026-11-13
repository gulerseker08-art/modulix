using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0041 dilim 1: firma alt profilleri. Firma yöneticisi alt profil açar, alan ve liste değeri seçenekleri ekler; etkinleştirince seçenekler
/// firmaya yalnızca eklenir. Pasife alınca eklenenler kalır; uygulanmış seçenek değişmez; başka firma görmez; yetkisiz kullanıcı yazamaz.
/// </summary>
public class SubProfileTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/customization/sub-profiles";

    [Fact]
    public async Task Admin_builds_a_sub_profile_previews_activates_it_and_the_options_reach_the_tenant_without_duplicates()
    {
        var (tenant, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Firmanın "Güzergâh" alanı zaten var: seçenek aynı adlı olduğundan ikinci alan açılmaz.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(
            CrmEntityKeys.Opportunity, "güzergâh", CustomFieldType.Text))).StatusCode);

        var created = await CreateAsync(client, "Kara taşımacılığı");
        Assert.Equal("kara_tasimaciligi", created.Code);
        Assert.Equal(SubProfileStatuses.Draft, created.Status);

        await AddFieldAsync(client, created.Id, "Güzergâh", CustomFieldType.Text);
        await AddFieldAsync(client, created.Id, "Araç tipi", CustomFieldType.Choice, required: true, choices: ["Tır", "Kamyon"]);
        var withOptions = await AddListValueAsync(client, created.Id, "Ön görüşme");
        Assert.Equal(3, withOptions.Options.Count);
        Assert.All(withOptions.Options, o => Assert.Null(o.AppliedAt));

        // Önizleme yazmaz: yalnızca yeni alan ve değer eklenecek görünür.
        var preview = (await client.GetFromJsonAsync<ProfilePlanDto>($"{Url}/{created.Id}/apply/preview"))!;
        Assert.Equal(["Araç tipi"], preview.Fields.Select(f => f.Label));
        Assert.Equal(["Ön görüşme"], preview.ListValues.Select(v => v.Label));
        Assert.Equal(["güzergâh"], (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Select(f => f.Label));

        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, withOptions.Revision);
        Assert.Equal(SubProfileStatuses.Active, active.Status);
        Assert.NotNull(active.ActivatedAt);
        Assert.All(active.Options, o => Assert.NotNull(o.AppliedAt));

        var fields = (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!;
        Assert.Equal(["güzergâh", "Araç tipi"], fields.Select(f => f.Label));
        var vehicleType = fields.Single(f => f.Label == "Araç tipi");
        Assert.True(vehicleType.Required);
        Assert.Equal(["Tır", "Kamyon"], vehicleType.Options.Select(o => o.Label));
        var stages = (await client.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!.Single(l => l.Key == CrmLists.OpportunityStage);
        Assert.Contains(stages.Values, v => v.Label == "Ön görüşme" && v.Category == CrmLists.StageCategories.Open);

        // Etkin alt profile eklenen seçenek hemen uygulanır.
        var later = await AddFieldAsync(client, created.Id, "Yük tipi", CustomFieldType.Text);
        Assert.Equal(0, later.PendingCount);
        Assert.Contains(
            (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Yük tipi");

        // Pasife alma eklenenleri silmez; yeni seçenek bekler, yeniden etkinleştirince uygulanır.
        var inactive = await SetStatusAsync(client, created.Id, SubProfileStatuses.Inactive, later.Revision);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Count);
        Assert.Equal(SubProfileStatuses.Inactive, inactive.Status);
        var pending = await AddFieldAsync(client, created.Id, "Teslim notu", CustomFieldType.Text);
        Assert.Equal(1, pending.PendingCount);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Teslim notu");
        var reactivated = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, pending.Revision);
        Assert.Equal(0, reactivated.PendingCount);
        Assert.Contains((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Teslim notu");
        Assert.Equal(inactive.Id, reactivated.Id);

        var list = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!;
        Assert.Equal(created.Id, Assert.Single(list).Id);
        Assert.Equal(5, list[0].OptionCount);
        Assert.Empty(list[0].Options);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.sub-profile").OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(AuditAction.Created, audit[0].Action);
        Assert.True(audit.Count >= 6);
        Assert.All(audit, a => Assert.Equal(admin.Id, a.UserId));
    }

    [Fact]
    public async Task Invalid_input_conflicts_and_unknown_ids_are_rejected_without_changing_anything()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateAsync(client, "Gümrüklü antrepo");

        // Ad zorunlu ve tekil; bilinmeyen faaliyet reddedilir.
        Assert.Contains("name", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest(" ", null, null))));
        Assert.Contains("name", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest("gümrüklü ANTREPO", null, null))));
        Assert.Contains("activityCode", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest("Deniz", null, "yok-boyle-faaliyet"))));

        // Seçenek doğrulaması: bilinmeyen kayıt türü, boş ad, seçim tipli alanda seçenek yok.
        Assert.Contains("targetKey", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, "yok.entity", "Alan", CustomFieldType.Text))));
        Assert.Contains("label", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, " ", CustomFieldType.Text))));
        Assert.Contains("choices", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Tip", CustomFieldType.Choice))));
        Assert.Contains("category", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.ListValue, CrmLists.OpportunityStage, "Değer", Category: "yok"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest("Bilinmeyen", "x", "y"))).StatusCode);

        // Aynı seçenek iki kez eklenmez.
        var first = await AddFieldAsync(client, created.Id, "Antrepo kodu", CustomFieldType.Text);
        Assert.Contains("label", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "antrepo KODU", CustomFieldType.Text))));
        Assert.Single((await GetAsync(client, created.Id)).Options);

        // Eski sürümle yazma 409; taslak doğrudan pasife alınamaz; geçersiz durum 400.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Yeni ad", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Inactive, first.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest("Uçtu", first.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{Url}/{created.Id}/apply", null)).StatusCode);

        // Doğru sürümle düzenleme olur; ad değişir, kod kalır.
        var renamed = await client.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Gümrüklü antrepo 2", "Açıklama", null, first.Revision));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedView = (await renamed.Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(("Gümrüklü antrepo 2", created.Code), (renamedView.Name, renamedView.Code));

        // Uygulanmış seçenek değişmez ve kaldırılmaz; uygulanmamış olan kaldırılır.
        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, renamedView.Revision);
        var applied = active.Options.Single();
        Assert.NotNull(applied.AppliedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Url}/{created.Id}/options/{applied.Id}", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Başka ad", CustomFieldType.Text))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Url}/{created.Id}/options/{applied.Id}")).StatusCode);
        var inactive = await SetStatusAsync(client, created.Id, SubProfileStatuses.Inactive, active.Revision);
        var waiting = await AddFieldAsync(client, created.Id, "Bekleyen", CustomFieldType.Text);
        var waitingOption = waiting.Options.Single(o => o.Label == "Bekleyen");
        var edited = await client.PutAsJsonAsync($"{Url}/{created.Id}/options/{waitingOption.Id}", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Bekleyen 2", CustomFieldType.Number));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(CustomFieldType.Number, (await edited.Content.ReadFromJsonAsync<SubProfileDto>())!.Options.Single(o => o.Id == waitingOption.Id).FieldType);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Url}/{created.Id}/options/{waitingOption.Id}")).StatusCode);
        Assert.Single((await GetAsync(client, created.Id)).Options);
        Assert.NotEqual(inactive.Revision, (await GetAsync(client, created.Id)).Revision);

        // Bilinmeyen kimlikler 404.
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{missing}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{created.Id}/options/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Url}/{missing}/options", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Alan", CustomFieldType.Text))).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_a_sub_profile()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var created = await CreateAsync(clientA, "Yalnız A'nın");
        await AddFieldAsync(clientA, created.Id, "Gizli alan", CustomFieldType.Text);

        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Ele geçirildi", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Active, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{created.Id}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostOptionAsync(clientB, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Sızdı", CustomFieldType.Text))).StatusCode);

        // B aynı adı ve aynı kodu bağımsız kullanabilir; A'nın verisi değişmez.
        var own = await CreateAsync(clientB, "Yalnız A'nın");
        Assert.Equal(created.Code, own.Code);
        Assert.Single((await GetAsync(clientA, created.Id)).Options);
        Assert.Empty((await clientB.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_read_or_write_sub_profiles()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        var created = await CreateAsync(adminClient, "Yetkili işi");

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsJsonAsync(Url, new SaveSubProfileRequest("İzinsiz", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("İzinsiz", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Active, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/{created.Id}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostOptionAsync(repClient, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "İzinsiz", CustomFieldType.Text))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.DeleteAsync($"{Url}/{created.Id}/options/{Guid.NewGuid()}")).StatusCode);

        // Hiçbir şey değişmedi.
        Assert.Single((await adminClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Empty((await GetAsync(adminClient, created.Id)).Options);
    }

    [Fact]
    public async Task Admin_adds_a_starter_as_a_draft_and_its_options_reach_the_tenant_only_when_activated()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var starters = (await client.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.Equal(["kara-tasimaciligi", "gumruklu-antrepo", "deniz-tasimaciligi"], starters.Select(s => s.Key));
        Assert.All(starters, s =>
        {
            Assert.False(s.Added);
            Assert.Empty(s.MissingModules);
            Assert.NotEmpty(s.Options);
        });
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], starters[0].Options);

        var response = await client.PostAsync($"{Url}/starters/kara-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(("kara-tasimaciligi", "Kara taşımacılığı", SubProfileStatuses.Draft), (created.Code, created.Name, created.Status));
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], created.Options.Select(o => o.Label));
        Assert.All(created.Options, o => Assert.Null(o.AppliedAt));
        Assert.Equal(["Tır", "Kamyon", "Kamyonet", "Frigorifik"], created.Options.Single(o => o.Label == "Araç tipi").Choices);

        // Eklemek hiçbir şeyi uygulamaz; kart "Eklendi" olur, diğer hazır alt profiller etkilenmez.
        Assert.Empty((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
        var after = (await client.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.Equal([true, false, false], after.Select(s => s.Added));

        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, created.Revision);
        Assert.All(active.Options, o => Assert.NotNull(o.AppliedAt));
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Select(f => f.Label));
    }

    [Fact]
    public async Task Starter_is_rejected_when_unknown_already_added_named_like_another_or_its_module_is_closed()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Url}/starters/yok", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"{Url}/starters/gumruklu-antrepo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{Url}/starters/gumruklu-antrepo", null)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!);

        // Başka koddan aynı adlı alt profil varsa hazır alt profil eklenmez ve yarım kayıt kalmaz.
        _ = await CreateAsync(client, "Deniz taşımacılığı");
        Assert.Contains("name", await ProblemAsync(await client.PostAsync($"{Url}/starters/deniz-tasimaciligi", null)));
        var names = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!.Select(p => p.Name).Order().ToList();
        Assert.Equal(["Deniz taşımacılığı", "Gümrüklü antrepo"], names);
        var deniz = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!.Single(p => p.Name == "Deniz taşımacılığı");
        Assert.Equal(0, deniz.OptionCount);

        // CRM kapalı bir firmada hazır alt profiller listelenir ama eklenemez; eksik modül adı söylenir.
        var tenant = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(closedAdmin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        using var closedClient = await LoginAsync(closedAdmin);
        var listed = (await closedClient.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.All(listed, s => Assert.Equal(["CRM"], s.MissingModules));
        Assert.Contains("key", await ProblemAsync(await closedClient.PostAsync($"{Url}/starters/kara-tasimaciligi", null)));
        Assert.Empty((await closedClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    [Fact]
    public async Task A_starter_added_by_one_tenant_is_invisible_to_another_which_can_add_its_own()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        var a = await clientA.PostAsync($"{Url}/starters/deniz-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var createdA = (await a.Content.ReadFromJsonAsync<SubProfileDto>())!;

        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.All((await clientB.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!, s => Assert.False(s.Added));
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{createdA.Id}")).StatusCode);

        // B kendi kopyasını ekleyince A'nınkine dokunmaz.
        var b = await clientB.PostAsync($"{Url}/starters/deniz-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.NotEqual(createdA.Id, (await b.Content.ReadFromJsonAsync<SubProfileDto>())!.Id);
        Assert.Single((await clientA.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_list_or_add_starters()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/starters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsync($"{Url}/starters/kara-tasimaciligi", null)).StatusCode);

        Assert.Empty((await adminClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    private static async Task<SubProfileDto> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Url, new SaveSubProfileRequest(name, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<SubProfileDto>($"{Url}/{id}"))!;

    private static Task<HttpResponseMessage> PostOptionAsync(HttpClient client, Guid id, SaveSubProfileOptionRequest request) =>
        client.PostAsJsonAsync($"{Url}/{id}/options", request);

    private static async Task<SubProfileDto> AddFieldAsync(
        HttpClient client, Guid id, string label, CustomFieldType type, bool required = false, string[]? choices = null)
    {
        var response = await PostOptionAsync(client, id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, label, type, required, Choices: choices));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> AddListValueAsync(HttpClient client, Guid id, string label)
    {
        var response = await PostOptionAsync(client, id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.ListValue, CrmLists.OpportunityStage, label, Category: CrmLists.StageCategories.Open));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> SetStatusAsync(HttpClient client, Guid id, string status, long revision)
    {
        var response = await client.PostAsJsonAsync($"{Url}/{id}/status", new SetSubProfileStatusRequest(status, revision));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, $"Beklenmeyen durum: {response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
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

        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead], rep);
        return (tenant, admin, rep);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
