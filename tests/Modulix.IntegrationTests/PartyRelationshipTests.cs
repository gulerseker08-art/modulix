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
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim E: firmanın ilişki rolleri, müşteri sektörü ve hizmet ihtiyacı. Üç alan <c>core.party</c> kaydının parçasıdır ve
/// kayıt boru hattından geçer (izin, kapsam, denetim); katalog global, değerler tenant verisidir.
/// </summary>
// Genel faaliyet kataloğunu (başlangıç paketi kurulumu) kullanan sınıflar sırayla çalışır: eşzamanlı kurulum tekil dizin hatası verir.
[Collection("SectorCatalog")]
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
    public async Task Service_need_accepts_only_activities_while_customer_sector_accepts_any_level()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);

        // Sektör ve grup hizmet ihtiyacı olamaz (doğrulama hatası, yazılmaz); müşteri sektörü olarak ikisi de olur.
        var rejected = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject
        {
            ["name"] = "Kısıt Firması", ["serviceNeeds"] = new JsonArray("lojistik", "depolama", "serbest-depolama"),
        }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var message = (await rejected.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors["serviceNeeds"][0];
        Assert.Contains("depolama", message);
        Assert.Contains("lojistik", message);
        Assert.DoesNotContain("serbest-depolama", message);

        var ok = await PostPartyAsync(client, new JsonObject
        {
            ["name"] = "Kısıt Firması", ["customerSectors"] = new JsonArray("lojistik", "depolama", "serbest-depolama"), ["serviceNeeds"] = new JsonArray("serbest-depolama"),
        });
        Assert.Equal(["depolama", "lojistik", "serbest-depolama"], Strings(ok.Values, "customerSectors"));
    }

    [Fact]
    public async Task Company_list_filters_by_role_sector_and_need_within_the_tenant_and_ignores_malformed_codes()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        async Task<string> CompanyAsync(HttpClient client, string name, JsonObject values)
        {
            values["name"] = name;
            var party = await PostPartyAsync(client, values);
            var created = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name, PartyId: party.Id));
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return name;
        }

        async Task<string[]> NamesAsync(HttpClient client, string query) =>
            [.. (await client.GetFromJsonAsync<CrmPage<CompanyDto>>($"/api/crm/companies?pageSize=100&{query}"))!.Items.Select(c => c.Name).Order()];

        var carrier = await CompanyAsync(clientA, "Filtre Taşıyıcı", new JsonObject { ["relationshipRoles"] = new JsonArray("tasiyici", "musteri"), ["customerSectors"] = new JsonArray("lojistik") });
        var supplier = await CompanyAsync(clientA, "Filtre Tedarikçi", new JsonObject { ["relationshipRoles"] = new JsonArray("tedarikci"), ["serviceNeeds"] = new JsonArray("gumruklu-antrepo") });
        await CompanyAsync(clientB, "Filtre Başka Tenant", new JsonObject { ["relationshipRoles"] = new JsonArray("tasiyici"), ["serviceNeeds"] = new JsonArray("gumruklu-antrepo") });

        Assert.Equal([carrier], await NamesAsync(clientA, "relationshipRole=tasiyici"));
        Assert.Equal([supplier], await NamesAsync(clientA, "relationshipRole=tedarikci"));
        Assert.Equal([carrier], await NamesAsync(clientA, "customerSector=lojistik"));
        Assert.Equal([supplier], await NamesAsync(clientA, "serviceNeed=gumruklu-antrepo"));
        Assert.Empty(await NamesAsync(clientA, "relationshipRole=tasi"));
        Assert.Equal([carrier], await NamesAsync(clientA, "relationshipRole=tasiyici&customerSector=lojistik"));
        Assert.Empty(await NamesAsync(clientA, "relationshipRole=tasiyici&serviceNeed=gumruklu-antrepo"));

        // Biçimsiz kod geçersiz süzgeçtir (diğer süzgeçler gibi 422); başka tenant'ın firması hiçbir süzgeçte görünmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clientA.GetAsync("/api/crm/companies?relationshipRole=B%C3%BCy%C3%BCk")).StatusCode);
        Assert.DoesNotContain("Filtre Başka Tenant", await NamesAsync(clientA, "search=Filtre"));
        Assert.Equal(["Filtre Başka Tenant"], await NamesAsync(clientB, "relationshipRole=tasiyici"));
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

    private const string Relationships = "/api/records/" + SharedEntities.PartyRelationship;

    private static async Task<HttpResponseMessage> PostRelationshipAsync(HttpClient client, Guid partyId, string role, JsonObject? more = null)
    {
        var values = new JsonObject { ["partyId"] = partyId.ToString(), ["roleCode"] = role };
        foreach (var (name, node) in more ?? [])
        {
            values[name] = node?.DeepClone();
        }

        return await client.PostAsJsonAsync(Relationships, new RecordWriteRequest(values));
    }

    private static async Task<IDictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors;
    }

    [Fact]
    public async Task A_relationship_keeps_status_dates_and_notes_per_party_and_role_and_updates_but_never_moves()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        var party = await PostPartyAsync(client, new JsonObject { ["name"] = "Durum Firması", ["relationshipRoles"] = new JsonArray("tedarikci", "tasiyici") });

        // Durum verilmezse aday başlar; ikinci rol kendi kaydını taşır.
        var prospect = await PostRelationshipAsync(client, party.Id, "tedarikci");
        Assert.Equal(HttpStatusCode.Created, prospect.StatusCode);
        var created = (await prospect.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal("Prospect", created.Values["status"]!.GetValue<string>());
        var active = await PostRelationshipAsync(client, party.Id, "tasiyici", new JsonObject
        {
            ["status"] = "Active", ["startDate"] = "2026-03-01", ["notes"] = "Yıllık sözleşme",
        });
        Assert.Equal(HttpStatusCode.Created, active.StatusCode);

        var listed = (await client.GetFromJsonAsync<RecordPageResponse>($"{Relationships}?partyId={party.Id}"))!;
        Assert.Equal(["tasiyici", "tedarikci"], listed.Items.Select(i => i.Values["roleCode"]!.GetValue<string>()).Order());
        var carrier = listed.Items.Single(i => i.Values["roleCode"]!.GetValue<string>() == "tasiyici");
        Assert.Equal("2026-03-01", carrier.Values["startDate"]!.GetValue<string>());
        Assert.Equal("Yıllık sözleşme", carrier.Values["notes"]!.GetValue<string>());
        Assert.Single((await client.GetFromJsonAsync<RecordPageResponse>($"{Relationships}?partyId={party.Id}&status=Active"))!.Items);

        // Durum ve tarihler güncellenir, boş tarih temizler; firma ve rol sonradan değişmez.
        var ended = await client.PutAsJsonAsync($"{Relationships}/{carrier.Id}", new RecordWriteRequest(
            new JsonObject { ["status"] = "Ended", ["endDate"] = "2026-09-30", ["startDate"] = "" }, carrier.Version));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        var reloaded = (await client.GetFromJsonAsync<RecordResponse>($"{Relationships}/{carrier.Id}"))!;
        Assert.Equal("Ended", reloaded.Values["status"]!.GetValue<string>());
        Assert.Equal("2026-09-30", reloaded.Values["endDate"]!.GetValue<string>());
        Assert.Null(reloaded.Values["startDate"]);

        var otherParty = await PostPartyAsync(client, new JsonObject { ["name"] = "Başka Firma" });
        var moved = await client.PutAsJsonAsync($"{Relationships}/{carrier.Id}", new RecordWriteRequest(
            new JsonObject { ["partyId"] = otherParty.Id.ToString() }, reloaded.Version));
        Assert.Contains("değiştirilemez", (await ErrorsAsync(moved))["partyId"][0]);
        var renamed = await client.PutAsJsonAsync($"{Relationships}/{carrier.Id}", new RecordWriteRequest(
            new JsonObject { ["roleCode"] = "tedarikci" }, reloaded.Version));
        Assert.Contains("değiştirilemez", (await ErrorsAsync(renamed))["roleCode"][0]);

        // Firma silinince ilişki kayıtları da gider (alt veri).
        var deleted = await client.DeleteAsync($"{Parties}/{party.Id}?version={party.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<RecordPageResponse>($"{Relationships}?partyId={party.Id}"))!.Items);
    }

    [Fact]
    public async Task Relationship_validation_rejects_unknown_roles_bad_values_duplicates_and_missing_parties()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        var party = await PostPartyAsync(client, new JsonObject { ["name"] = "Doğrulama Firması" });

        Assert.Contains("yok-rol", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "yok-rol")))["roleCode"][0]);
        Assert.Contains("Prospect, Active", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri", new JsonObject { ["status"] = "Bilinmeyen" })))["status"][0]);
        Assert.Contains("Prospect, Active", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri", new JsonObject { ["status"] = "2" })))["status"][0]);
        Assert.Contains("yyyy-AA-gg", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri", new JsonObject { ["startDate"] = "01.03.2026" })))["startDate"][0]);
        Assert.Contains("önce olamaz", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri", new JsonObject
        {
            ["startDate"] = "2026-06-01", ["endDate"] = "2026-05-31",
        })))["endDate"][0]);
        Assert.Contains("Firma bulunamadı", (await ErrorsAsync(await PostRelationshipAsync(client, Guid.NewGuid(), "musteri")))["partyId"][0]);

        var missing = await ErrorsAsync(await client.PostAsJsonAsync(Relationships, new RecordWriteRequest(new JsonObject())));
        Assert.Contains("partyId", missing.Keys);
        Assert.Contains("roleCode", missing.Keys);
        Assert.Contains("Bilinmeyen alan", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri", new JsonObject { ["terms"] = "30 gün" })))["terms"][0]);

        // Aynı firma ve rol için ikinci kayıt açılamaz; hatalı denemelerin hiçbiri yazılmamıştır.
        Assert.Equal(HttpStatusCode.Created, (await PostRelationshipAsync(client, party.Id, "musteri")).StatusCode);
        Assert.Contains("zaten var", (await ErrorsAsync(await PostRelationshipAsync(client, party.Id, "musteri")))["roleCode"][0]);
        Assert.Single((await client.GetFromJsonAsync<RecordPageResponse>($"{Relationships}?partyId={party.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Relationships}?status=Bilinmeyen")).StatusCode);
    }

    [Fact]
    public async Task Relationships_of_another_tenant_are_invisible_and_cannot_be_attached_to_its_parties()
    {
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        var partyA = await PostPartyAsync(clientA, new JsonObject { ["name"] = "A İlişkili" });
        var relationA = (await (await PostRelationshipAsync(clientA, partyA.Id, "musteri", new JsonObject { ["status"] = "Active" })).Content.ReadFromJsonAsync<RecordResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Relationships}/{relationA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"{Relationships}/{relationA.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "Ended" }, relationA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"{Relationships}/{relationA.Id}?version={relationA.Version}")).StatusCode);
        Assert.Empty((await clientB.GetFromJsonAsync<RecordPageResponse>($"{Relationships}?partyId={partyA.Id}"))!.Items);

        // B, A'nın firmasına kayıt açamaz (firma yok sayılır); aynı rol kodu iki tenant'ta ayrı ayrı kullanılabilir.
        Assert.Contains("Firma bulunamadı", (await ErrorsAsync(await PostRelationshipAsync(clientB, partyA.Id, "musteri")))["partyId"][0]);
        var partyB = await PostPartyAsync(clientB, new JsonObject { ["name"] = "B İlişkili" });
        Assert.Equal(HttpStatusCode.Created, (await PostRelationshipAsync(clientB, partyB.Id, "musteri")).StatusCode);
        Assert.Equal("Active", (await clientA.GetFromJsonAsync<RecordResponse>($"{Relationships}/{relationA.Id}"))!.Values["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Relationship_writes_need_company_write_permission_and_out_of_scope_companies_show_no_status()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var scoped = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own, scoped);
        using var adminClient = await LoginAsync(admin);
        using var readerClient = await LoginAsync(reader);
        using var scopedClient = await LoginAsync(scoped);

        // CRM firması yönetici sorumluluğunda; ilişki kaydı o firmanın kuralına uyar.
        var party = await PostPartyAsync(adminClient, new JsonObject { ["name"] = "Kapsam Firması", ["relationshipRoles"] = new JsonArray("musteri") });
        var company = await adminClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Kapsam Firması", PartyId: party.Id));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var relation = (await (await PostRelationshipAsync(adminClient, party.Id, "musteri", new JsonObject { ["status"] = "Active", ["notes"] = "Gizli not" })).Content.ReadFromJsonAsync<RecordResponse>())!;

        // Salt okuyan her şeyi görür ama yazamaz; oturumsuz çağıran hiçbir şey göremez.
        Assert.Equal("Gizli not", (await readerClient.GetFromJsonAsync<RecordResponse>($"{Relationships}/{relation.Id}"))!.Values["notes"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRelationshipAsync(readerClient, party.Id, "tedarikci")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"{Relationships}/{relation.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "Ended" }, relation.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.DeleteAsync($"{Relationships}/{relation.Id}?version={relation.Version}")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Relationships)).StatusCode);

        // Yalnızca kendi kayıtlarını gören kullanıcı başkasının firmasının durumunu, tarihini ve notunu göremez; firma kimliği kalır.
        var redacted = (await scopedClient.GetFromJsonAsync<RecordResponse>($"{Relationships}/{relation.Id}"))!;
        Assert.Equal("musteri", redacted.Values["roleCode"]!.GetValue<string>());
        Assert.Null(redacted.Values["status"]);
        Assert.Null(redacted.Values["notes"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await scopedClient.PutAsJsonAsync($"{Relationships}/{relation.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "Ended" }, relation.Version))).StatusCode);
        Assert.Equal("Active", (await adminClient.GetFromJsonAsync<RecordResponse>($"{Relationships}/{relation.Id}"))!.Values["status"]!.GetValue<string>());
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
