using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim E: firmanın ilişki rolleri, müşteri sektörü ve hizmet ihtiyacı. Üç alan <c>core.party</c> kaydının parçasıdır ve
/// kayıt boru hattından geçer (izin, kapsam, denetim); katalog global, değerler tenant verisidir.
/// </summary>
public class PartyRelationshipTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;

    [Fact]
    public async Task A_party_keeps_normalized_roles_sector_and_service_need_and_the_catalog_lists_the_seed_roles()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        var roles = (await platform.GetFromJsonAsync<List<RelationshipRoleDto>>("/api/platform/sectors/relationship-roles"))!;
        Assert.Contains(roles, r => r.Code == "musteri" && r.IsActive);
        Assert.Contains(roles, r => r.Code == "tasiyici");

        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        Assert.Contains((await client.GetFromJsonAsync<List<RelationshipRoleDto>>("/api/relationship-roles"))!, r => r.Code == "tedarikci");

        // Tekrar ve sıra normalleşir; bir firma aynı anda hem müşteri hem taşıyıcı olabilir. Serbest metin sektör alanı bundan etkilenmez.
        var created = await PostPartyAsync(client, new JsonObject
        {
            ["name"] = "Anka Taşımacılık",
            ["relationshipRoles"] = new JsonArray("tasiyici", "musteri", "musteri"),
            ["customerSectors"] = new JsonArray("lojistik"),
            ["serviceNeeds"] = new JsonArray("gumruklu-antrepo", "karayolu-tasimaciligi"),
        });
        Assert.Equal(["musteri", "tasiyici"], Strings(created.Values, "relationshipRoles"));
        Assert.Equal(["lojistik"], Strings(created.Values, "customerSectors"));
        Assert.Equal(["gumruklu-antrepo", "karayolu-tasimaciligi"], Strings(created.Values, "serviceNeeds"));

        // Hizmet ihtiyacı roldan bağımsızdır: rol değişince ihtiyaç kalır; boş liste alanı temizler.
        var updated = await client.PutAsJsonAsync($"{Parties}/{created.Id}", new RecordWriteRequest(
            new JsonObject { ["relationshipRoles"] = new JsonArray("tedarikci"), ["customerSectors"] = new JsonArray() }, created.Version));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var loaded = (await client.GetFromJsonAsync<RecordResponse>($"{Parties}/{created.Id}"))!;
        Assert.Equal(["tedarikci"], Strings(loaded.Values, "relationshipRoles"));
        Assert.Empty(Strings(loaded.Values, "customerSectors"));
        Assert.Equal(["gumruklu-antrepo", "karayolu-tasimaciligi"], Strings(loaded.Values, "serviceNeeds"));
    }

    [Fact]
    public async Task Unknown_roles_and_activities_and_malformed_lists_are_rejected_without_writing()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);

        async Task<IDictionary<string, string[]>> RejectedAsync(JsonObject values)
        {
            values["name"] = "Hatalı Firma";
            var response = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(values));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors;
        }

        Assert.Contains("yok-rol", (await RejectedAsync(new JsonObject { ["relationshipRoles"] = new JsonArray("yok-rol") }))["relationshipRoles"][0]);
        Assert.Contains("yok-faaliyet", (await RejectedAsync(new JsonObject { ["customerSectors"] = new JsonArray("yok-faaliyet") }))["customerSectors"][0]);
        Assert.Contains("yok-faaliyet", (await RejectedAsync(new JsonObject { ["serviceNeeds"] = new JsonArray("yok-faaliyet") }))["serviceNeeds"][0]);
        Assert.Contains("Geçersiz kod", (await RejectedAsync(new JsonObject { ["relationshipRoles"] = new JsonArray("Büyük Harf") }))["relationshipRoles"][0]);
        Assert.Contains("Metin listesi", (await RejectedAsync(new JsonObject { ["relationshipRoles"] = "musteri" }))["relationshipRoles"][0]);
        var tooMany = new JsonArray([.. Enumerable.Range(0, 21).Select(i => (JsonNode?)JsonValue.Create($"rol-{i}"))]);
        Assert.Contains("En çok 20", (await RejectedAsync(new JsonObject { ["relationshipRoles"] = tooMany }))["relationshipRoles"][0]);

        var none = (await client.GetFromJsonAsync<RecordPageResponse>($"{Parties}?search=Hatalı%20Firma"))!;
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_relationship_data_and_its_own_parties_stay_separate()
    {
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        var partyA = await PostPartyAsync(clientA, new JsonObject { ["name"] = "A Firması", ["relationshipRoles"] = new JsonArray("musteri") });
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Parties}/{partyA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync(
            $"{Parties}/{partyA.Id}", new RecordWriteRequest(new JsonObject { ["relationshipRoles"] = new JsonArray("tedarikci") }, partyA.Version))).StatusCode);

        var partyB = await PostPartyAsync(clientB, new JsonObject { ["name"] = "B Firması" });
        Assert.Empty(Strings(partyB.Values, "relationshipRoles"));
        Assert.Equal(["musteri"], Strings((await clientA.GetFromJsonAsync<RecordResponse>($"{Parties}/{partyA.Id}"))!.Values, "relationshipRoles"));
    }

    [Fact]
    public async Task Only_authorized_callers_write_party_roles_and_only_platform_manages_the_role_catalog()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, reader);
        using var adminClient = await LoginAsync(admin);
        using var readerClient = await LoginAsync(reader);
        var party = await PostPartyAsync(adminClient, new JsonObject { ["name"] = "Yetki Firması", ["relationshipRoles"] = new JsonArray("musteri") });

        // Salt okuyan rolü görür ama değiştiremez.
        Assert.Equal(["musteri"], Strings((await readerClient.GetFromJsonAsync<RecordResponse>($"{Parties}/{party.Id}"))!.Values, "relationshipRoles"));
        var denied = await readerClient.PutAsJsonAsync($"{Parties}/{party.Id}", new RecordWriteRequest(new JsonObject { ["relationshipRoles"] = new JsonArray("tedarikci") }, party.Version));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(["musteri"], Strings((await adminClient.GetFromJsonAsync<RecordResponse>($"{Parties}/{party.Id}"))!.Values, "relationshipRoles"));

        // Katalog yönetimi yalnızca platformdadır; firma yöneticisi ve oturumsuz çağıran yazamaz, oturumsuz okuyamaz.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/relationship-roles")).StatusCode);
        foreach (var client in new[] { adminClient, anonymous })
        {
            var status = (await client.PostAsJsonAsync("/api/platform/sectors/relationship-roles", new CreateRelationshipRoleRequest("deneme", "Deneme"))).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"rol ekleme: {status}");
            status = (await client.PutAsJsonAsync("/api/platform/sectors/relationship-roles/musteri/active", new SetActiveRequest(false))).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"rol pasife alma: {status}");
        }

        // Platform rolü ekler; aynı kod çakışır; pasife alınan rol kayıtta kalır, yeni kayda hâlâ kod olarak yazılabilir (kodla başvuru).
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var code = $"rol-{Guid.NewGuid():N}"[..20];
        var added = await platform.PostAsJsonAsync("/api/platform/sectors/relationship-roles", new CreateRelationshipRoleRequest(code, "Deneme rolü"));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/sectors/relationship-roles", new CreateRelationshipRoleRequest(code, "Yine"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/sectors/relationship-roles", new CreateRelationshipRoleRequest("Kötü Kod", "Ad"))).StatusCode);

        var withNew = await PostPartyAsync(adminClient, new JsonObject { ["name"] = "Yeni Rollü", ["relationshipRoles"] = new JsonArray(code) });
        var off = await platform.PutAsJsonAsync($"/api/platform/sectors/relationship-roles/{code}/active", new SetActiveRequest(false));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<RelationshipRoleDto>())!.IsActive);
        Assert.Equal([code], Strings((await adminClient.GetFromJsonAsync<RecordResponse>($"{Parties}/{withNew.Id}"))!.Values, "relationshipRoles"));
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync("/api/platform/sectors/relationship-roles/yok/active", new SetActiveRequest(true))).StatusCode);
    }

    [Fact]
    public async Task Any_tenant_user_reads_active_activity_options_but_not_inactive_ones_and_anonymous_cannot()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        var code = $"sek-{Guid.NewGuid():N}"[..20];
        var created = await platform.PostAsJsonAsync("/api/platform/sectors/nodes", new CreateActivityNodeRequest(code, null, "Sector", "Geçici sektör", null, 999));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var node = (await created.Content.ReadFromJsonAsync<ActivityNodeDto>())!;

        // Yalnızca firma okuma izni olan kullanıcı (özelleştirme yetkisi yok) seçenekleri görür; ad ve konum dışında bilgi dönmez.
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, reader);
        using var readerClient = await LoginAsync(reader);
        var options = (await readerClient.GetFromJsonAsync<List<ActivityOptionDto>>("/api/activity-options"))!;
        Assert.Contains(options, o => o.Code == code && o.ParentCode is null && o.Kind == "Sector");
        var logistics = options.Single(o => o.Code == "lojistik");
        Assert.Contains(options, o => o.ParentCode == logistics.Code);

        // Pasife alınan düğüm seçenek olarak dönmez (kayıtlardaki eski değer korunur, yeni seçim yapılamaz).
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{node.Id}/active", new SetActiveRequest(false))).StatusCode);
        Assert.DoesNotContain((await readerClient.GetFromJsonAsync<List<ActivityOptionDto>>("/api/activity-options"))!, o => o.Code == code);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/activity-options")).StatusCode);
    }

    private static string[] Strings(JsonObject values, string name) =>
        values[name] is JsonArray array ? [.. array.Select(n => n!.GetValue<string>())] : [];

    private static async Task<RecordResponse> PostPartyAsync(HttpClient client, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> CreateTenantAdminAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        using var scope = factory.OpenScope(tenant.Id);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        return (tenant, admin);
    }

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
