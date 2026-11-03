using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 3 (K8, K9): kapsam dışı ortak firma ve kişileri gizleyen roller. Gizleme açıkken bu kayıtlar genel ortak kayıt
/// uçlarında (liste, sayım, arama, süzgeç, tekil okuma, değiştirme, silme) ve mükerrer önerisinde hiç görünmez; kapalıyken bugünkü
/// asgari kimlik davranışı korunur. Çakışma iletileri kaydın adını, kimliğini ya da sahibini taşımaz ve kapsam içi/dışı kayıt için
/// aynıdır. Kişinin tüm rolleri gizlemiyorsa gizlenmez.
/// </summary>
public class SharedRecordHidingTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Persons = "/api/records/" + SharedEntities.Person;
    private const string Plain = "Bu vergi numarasıyla kayıtlı bir firma var.";

    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite];

    [Fact]
    public async Task Hiding_roles_remove_out_of_scope_parties_and_people_from_every_shared_endpoint()
    {
        var t = await SetUpAsync();
        using var a = await LoginAsync(t.RepA);
        using var hiding = await LoginAsync(t.RepB);
        using var showing = await LoginAsync(t.RepC);
        using var admin = await LoginAsync(t.Admin);

        var company = await CreateCompanyAsync(a, "Gizli Ltd.", "5550001110");
        var contact = await CreateContactAsync(a, "Gizli", "Kişi", company.Id);

        // Sahip tam profili görür.
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);

        // Gizleyen rol: tekil okuma, değiştirme, silme "bulunamadı"; liste, sayım, arama ve süzgeçte yok.
        Assert.Equal(HttpStatusCode.NotFound, (await hiding.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hiding.GetAsync($"{Persons}/{contact.PersonId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hiding.PutAsJsonAsync(
            $"{Parties}/{company.PartyId}", new RecordWriteRequest(new JsonObject { ["city"] = "Ele geçir" }, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hiding.DeleteAsync($"{Parties}/{company.PartyId}?version=1")).StatusCode);

        var bySearch = (await hiding.GetFromJsonAsync<RecordPageResponse>($"{Parties}?search=Gizli"))!;
        Assert.Equal(0, bySearch.TotalCount);
        Assert.Empty(bySearch.Items);
        var byTax = (await hiding.GetFromJsonAsync<RecordPageResponse>($"{Parties}?taxNumber=5550001110"))!;
        Assert.Equal(0, byTax.TotalCount);
        Assert.Equal(0, (await hiding.GetFromJsonAsync<RecordPageResponse>($"{Persons}?search=Gizli"))!.TotalCount);

        // Aynı veriyi gösteren rol bugünkü davranışı korur: asgari kimlik (ad, vergi no) görünür, iletişim bilgisi görünmez.
        var minimal = (await showing.GetFromJsonAsync<RecordResponse>($"{Parties}/{company.PartyId}"))!;
        Assert.Equal("Gizli Ltd.", minimal.Values["name"]!.GetValue<string>());
        Assert.Equal("5550001110", minimal.Values["taxNumber"]!.GetValue<string>());
        Assert.Null(minimal.Values["city"]);
        Assert.Equal(1, (await showing.GetFromJsonAsync<RecordPageResponse>($"{Parties}?search=Gizli"))!.TotalCount);

        // Yönetici (tüm kayıtlar) kısıtlanmaz.
        Assert.Equal(1, (await admin.GetFromJsonAsync<RecordPageResponse>($"{Parties}?search=Gizli"))!.TotalCount);

        // Kişinin kendi kaydı gizlenmez: toplam yalnızca görebildiği kayıtları sayar.
        var own = await CreateCompanyAsync(hiding, "Kendi Ltd.", "5550002220");
        var page = (await hiding.GetFromJsonAsync<RecordPageResponse>($"{Parties}?pageSize=50"))!;
        Assert.Equal(new[] { own.PartyId!.Value }, page.Items.Select(i => i.Id));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Duplicate_suggestions_do_not_name_out_of_scope_records_for_hiding_roles()
    {
        var t = await SetUpAsync();
        using var a = await LoginAsync(t.RepA);
        using var hiding = await LoginAsync(t.RepB);
        using var showing = await LoginAsync(t.RepC);
        await CreateCompanyAsync(a, "Mükerrer Gizli Ltd.", "5550003330");

        var hidden = (await hiding.GetFromJsonAsync<List<DuplicateCandidateDto>>("/api/crm/companies/duplicates?taxNumber=5550003330"))!;
        Assert.Empty(hidden);

        // Gizlemeyen rol bugünkü asgari kimlik önerisini alır (kayıt başka erişimde olduğu için ad görünür).
        var shown = (await showing.GetFromJsonAsync<List<DuplicateCandidateDto>>("/api/crm/companies/duplicates?taxNumber=5550003330"))!;
        Assert.Equal("Mükerrer Gizli Ltd.", Assert.Single(shown).Name);
    }

    [Fact]
    public async Task Conflict_messages_name_nothing_and_are_identical_for_in_scope_and_out_of_scope_records()
    {
        var t = await SetUpAsync();
        using var a = await LoginAsync(t.RepA);
        using var hiding = await LoginAsync(t.RepB);
        using var showing = await LoginAsync(t.RepC);
        using var admin = await LoginAsync(t.Admin);
        await CreateCompanyAsync(a, "Başkasının Ltd.", "5550004440");
        await CreateCompanyAsync(hiding, "Benim Ltd.", "5550005550");

        // Gizleyen rol: kapsam dışı ve kapsam içi çakışma aynı iletiyi alır; ileti yetkiliye yönlendirir, hiçbir kayıt adı taşımaz.
        var outOfScope = await TaxConflictAsync(hiding, "5550004440");
        var inScope = await TaxConflictAsync(hiding, "5550005550");
        Assert.Equal(outOfScope, inScope);
        Assert.StartsWith(Plain, outOfScope);
        Assert.Contains("yetkili (merkez) kullanıcıya başvurun", outOfScope);
        Assert.DoesNotContain("Başkasının", outOfScope);
        Assert.DoesNotContain("Benim", outOfScope);

        // Genel ortak kayıt ucunda da aynı: kayıt oluşturma çakışması ad vermez.
        var viaShared = await hiding.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject { ["name"] = "Kopya", ["taxNumber"] = "5550004440" }, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, viaShared.StatusCode);
        var sharedBody = await viaShared.Content.ReadAsStringAsync();
        Assert.Contains("yetkili (merkez) kullanıcıya başvurun", sharedBody);
        Assert.DoesNotContain("Başkasının", sharedBody);

        // Gizlemeyen rol ve yönetici bugünkü sade iletiyi alır.
        Assert.Equal(Plain, await TaxConflictAsync(showing, "5550004440"));
        Assert.Equal(Plain, await TaxConflictAsync(admin, "5550004440"));
    }

    [Fact]
    public async Task A_person_with_any_role_that_shows_minimal_identity_keeps_seeing_it()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        using var a = await LoginAsync(t.RepA);
        var company = await CreateCompanyAsync(a, "Karma Ltd.", "5550006660");

        // RepB'ye gizlemeyen ikinci rol da verilince ("tüm roller gizliyorsa" kuralı) kayıt asgari kimlikle yeniden görünür.
        using var mixed = await LoginAsync(t.RepB);
        Assert.Equal(HttpStatusCode.NotFound, (await mixed.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        var extra = await CreateRoleAsync(admin, "Göstere izin", [ActivitiesRead], hide: false);
        await SetRolesAsync(admin, t.RepB, t.Tenant, t.HidingRole.Id, extra.Id);
        Assert.Equal(HttpStatusCode.OK, (await mixed.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);

        // Gizleyen rol tek kalınca yeniden gizlenir.
        await SetRolesAsync(admin, t.RepB, t.Tenant, t.HidingRole.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await mixed.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
    }

    [Fact]
    public async Task Department_roles_can_hide_other_departments_customers_from_dealers()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var root = (await admin.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealerA = await CreateUnitAsync(admin, root.Id, "Bayi A");
        var dealerB = await CreateUnitAsync(admin, root.Id, "Bayi B");
        var dealerRole = await CreateRoleAsync(admin, "Bayi", SalesAccess, hide: true, scope: SharedScope.Unit, level: RoleAssignLevel.Unit);
        Assert.True(dealerRole.HideOutOfScopeShared);
        foreach (var unit in new[] { dealerA, dealerB })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
                $"/api/business-units/{unit.Id}/roles", new SetBusinessUnitRolesRequest([new BusinessUnitRoleItem(dealerRole.Id)]))).StatusCode);
        }

        var a1 = await factory.CreateUserAsync(t.Tenant);
        var a2 = await factory.CreateUserAsync(t.Tenant);
        var b1 = await factory.CreateUserAsync(t.Tenant);
        foreach (var (user, unit) in new[] { (a1, dealerA), (a2, dealerA), (b1, dealerB) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
                $"/api/users/{await factory.GetMembershipIdAsync(user.Id, t.Tenant.Id)}/business-unit",
                new SetMembershipBusinessUnitRequest(unit.Id))).StatusCode);
        }

        using var a1Client = await LoginAsync(a1);
        using var a2Client = await LoginAsync(a2);
        using var b1Client = await LoginAsync(b1);
        var company = await CreateCompanyAsync(a1Client, "A bayisi müşterisi", "5550007770");

        // Aynı departmandaki iş arkadaşı görür; diğer bayi ne ad ne sayı ne mükerrer önerisi ne de tekil okuma ile görür.
        Assert.Equal(HttpStatusCode.OK, (await a2Client.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b1Client.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        Assert.Equal(0, (await b1Client.GetFromJsonAsync<RecordPageResponse>($"{Parties}?search=A%20bayisi"))!.TotalCount);
        Assert.Empty((await b1Client.GetFromJsonAsync<List<DuplicateCandidateDto>>("/api/crm/companies/duplicates?taxNumber=5550007770"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b1Client.GetAsync($"/api/crm/companies/{company.Id}")).StatusCode);
        Assert.Equal(0, (await b1Client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=A%20bayisi"))!.TotalCount);
    }

    [Fact]
    public async Task The_flag_is_a_role_setting_managed_with_role_permissions_and_kept_per_tenant()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);

        // Rol listesi ve ayrıntısı ayarı gösterir; kaydetmek ayarı değiştirir, kapsam verilmezse ayar korunur.
        var role = await CreateRoleAsync(admin, "Ayarlı", SalesAccess, hide: true);
        var kept = await admin.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Ayarlı", SalesAccess));
        Assert.True((await kept.Content.ReadFromJsonAsync<RoleDto>())!.HideOutOfScopeShared);
        var off = await admin.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Ayarlı", SalesAccess, null, null, false));
        Assert.False((await off.Content.ReadFromJsonAsync<RoleDto>())!.HideOutOfScopeShared);
        Assert.False((await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.Id == role.Id).HideOutOfScopeShared);

        // Yönetici sistem rolünde ayar değiştirilemez (zaten tüm kayıtları görür).
        var system = (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.IsSystem);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(
            $"/api/roles/{system.Id}", new SaveRoleRequest(system.Name, [], null, null, true))).StatusCode);

        // Rol yönetme izni olmayan kişi ayarı değiştiremez.
        using var rep = await LoginAsync(t.RepA);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PutAsJsonAsync(
            $"/api/roles/{role.Id}", new SaveRoleRequest("Ayarlı", SalesAccess, null, null, true))).StatusCode);

        // Başka tenant'ın yöneticisi bu rolü göremez ve değiştiremez; kendi tenant'ında ayar bağımsızdır.
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.DoesNotContain((await otherClient.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!, r => r.Id == role.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync(
            $"/api/roles/{role.Id}", new SaveRoleRequest("Ele geçir", SalesAccess, null, null, true))).StatusCode);

        // Denetim kaydı ayar değişimini yazar.
        using var scope = factory.OpenScope(t.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var changes = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.Role && a.RecordId == role.Id && a.Action == AuditAction.Updated)
            .ToListAsync();
        Assert.Contains(changes, a => a.Changes.ToString().Contains("hideOutOfScopeShared"));
    }

    [Fact]
    public async Task Hidden_records_of_one_tenant_never_leak_into_another_tenants_shared_endpoints()
    {
        var t = await SetUpAsync();
        using var a = await LoginAsync(t.RepA);
        var company = await CreateCompanyAsync(a, "Tenant A firması", "5550008880");

        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        Assert.Equal(0, (await otherClient.GetFromJsonAsync<RecordPageResponse>($"{Parties}?taxNumber=5550008880"))!.TotalCount);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<DuplicateCandidateDto>>("/api/crm/companies/duplicates?taxNumber=5550008880"))!);
        // Aynı vergi numarası başka tenant'ta serbesttir (benzersizlik tenant içindedir).
        Assert.Equal(HttpStatusCode.Created, (await otherClient.PostAsJsonAsync(
            "/api/crm/companies", new SaveCompanyRequest("Başka tenant", TaxNumber: "5550008880"))).StatusCode);
    }

    private sealed record Setup(
        Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser RepA, ModulixUser RepB, ModulixUser RepC, RoleDto HidingRole);

    // RepA ve RepB gizleyen rolde, RepC gizlemeyen rolde; üçü de yalnızca kendi kayıtlarını görür.
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        var repC = await factory.CreateUserAsync(tenant);
        // RepA yalnızca satış rolüne sahiptir; rol yönetme izni verilmez (yetki testi).
        using var adminClient = await LoginAsync(admin);
        var hiding = await CreateRoleAsync(adminClient, "Gizleyen", SalesAccess, hide: true);
        var showing = await CreateRoleAsync(adminClient, "Gösteren", SalesAccess, hide: false);
        await SetRolesAsync(adminClient, repA, tenant, hiding.Id);
        await SetRolesAsync(adminClient, repB, tenant, hiding.Id);
        await SetRolesAsync(adminClient, repC, tenant, showing.Id);
        return new Setup(tenant, admin, repA, repB, repC, hiding);
    }

    private async Task SetRolesAsync(HttpClient admin, ModulixUser user, Core.Platform.Tenant tenant, params Guid[] roleIds)
    {
        var membership = await factory.GetMembershipIdAsync(user.Id, tenant.Id);
        var response = await admin.PutAsJsonAsync($"/api/memberships/{membership}/roles", new SetMembershipRolesRequest(roleIds));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<RoleDto> CreateRoleAsync(
        HttpClient client, string name, string[] permissions, bool hide, SharedScope scope = SharedScope.Own, RoleAssignLevel level = RoleAssignLevel.User)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(name, permissions, scope, level, hide));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private static async Task<BusinessUnitDto> CreateUnitAsync(HttpClient client, Guid parentId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(parentId, name, null, BusinessUnitKindLabels.Dealer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name, string taxNumber)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name, TaxNumber: taxNumber));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<ContactDto> CreateContactAsync(HttpClient client, string first, string last, Guid companyId)
    {
        var response = await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last, companyId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContactDto>())!;
    }

    // Aynı vergi numarasıyla yeni firma açmayı dener ve doğrulama hatasındaki vergi numarası iletisini döner.
    private static async Task<string> TaxConflictAsync(HttpClient client, string taxNumber)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Çakışan {Guid.NewGuid():N}"[..14], TaxNumber: taxNumber));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        return body["errors"]!["taxNumber"]![0]!.GetValue<string>();
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
