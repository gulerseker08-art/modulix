using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0030 dilim 2b: rol, kayıt türüne göre kapsam istisnası taşır; istisna ve varsayılan kapsam platform tavanını aşamaz (409);
/// değişmeyen kapsamlar sonradan daralan tavanda kilitlenmez; işlemi yapan kendi kapsamından geniş istisna veremez; istisna
/// denetim kaydına yazılır, rolle birlikte silinir ve başka firmayı etkilemez.
/// </summary>
public class RoleScopeOverrideTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Company = "crm.company";
    private const string Contact = "crm.contact";

    [Fact]
    public async Task Override_changes_the_effective_scope_per_record_type_and_is_kept_when_omitted()
    {
        var (tenant, admin) = await SetUpTenantAsync();
        var rep = await factory.CreateUserAsync(tenant);
        using var adminClient = await LoginAsync(admin);
        await CreateCompanyAsync(adminClient, "Firma 1");
        await CreateCompanyAsync(adminClient, "Firma 2");

        // Varsayılan "yalnızca kendi kayıtları"; firmalarda istisna "tüm kayıtlar".
        var created = await adminClient.PostAsJsonAsync("/api/roles",
            Request(SharedScope.Own, new() { [Company] = SharedScope.All }, [CompaniesRead]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;
        Assert.Equal(SharedScope.All, role.ScopeOverrides![Company]);
        await AssignAsync(tenant, rep, role.Id);

        using var repClient = await LoginAsync(rep);
        Assert.Equal(2, await CountCompaniesAsync(repClient));

        // İstisna gönderilmezse (null) değişmez; ad değişikliği istisnayı silmez.
        var renamed = await adminClient.PutAsJsonAsync($"/api/roles/{role.Id}",
            new SaveRoleRequest(role.Name + "-2", [CompaniesRead], null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(SharedScope.All, (await renamed.Content.ReadFromJsonAsync<RoleDto>())!.ScopeOverrides![Company]);
        Assert.Equal(2, await CountCompaniesAsync(repClient));

        // Boş küme istisnaları kaldırır; rol varsayılan kapsamına döner.
        var cleared = await adminClient.PutAsJsonAsync($"/api/roles/{role.Id}",
            new SaveRoleRequest(role.Name + "-2", [CompaniesRead], null, null, null, new Dictionary<string, SharedScope>()));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<RoleDto>())!.ScopeOverrides!);
        Assert.Equal(0, await CountCompaniesAsync(repClient));

        // Listede de görünür.
        var listed = (await adminClient.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.Id == role.Id);
        Assert.Empty(listed.ScopeOverrides!);
    }

    [Fact]
    public async Task Invalid_overrides_are_rejected_and_nothing_is_written()
    {
        var (tenant, admin) = await SetUpTenantAsync();
        using var client = await LoginAsync(admin);
        var before = (await client.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Count;

        // Bilinmeyen tür, biçimsiz anahtar, alt kayıt türü (kapsamı ana türünden gelir), geçersiz kapsam.
        foreach (var overrides in new Dictionary<string, SharedScope>[]
        {
            new() { ["crm.nothing"] = SharedScope.Own },
            new() { ["company"] = SharedScope.Own },
            new() { ["crm.company-contact"] = SharedScope.Own },
            new() { [Company] = (SharedScope)99 },
        })
        {
            var response = await client.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, overrides));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(before, (await client.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Count);
        Assert.Equal(0, await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM [core].[RoleScopeOverrides] WHERE [TenantId] = @t", ("@t", tenant.Id)));
    }

    [Fact]
    public async Task Overrides_and_default_scope_cannot_exceed_the_platform_limit()
    {
        var (tenant, admin) = await SetUpTenantAsync();
        using var client = await LoginAsync(admin);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());

        // Tavan konmadan geniş kapsam serbest.
        var wide = await client.PostAsJsonAsync("/api/roles", Request(SharedScope.All));
        Assert.Equal(HttpStatusCode.Created, wide.StatusCode);
        var wideRole = (await wide.Content.ReadFromJsonAsync<RoleDto>())!;

        await SetLimitAsync(platform, tenant.Id, Company, SharedScope.Own);

        // Varsayılan kapsam tavanı aşıyor: 409.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/roles", Request(SharedScope.All))).StatusCode);

        // İstisna tavanı aşıyor (varsayılan uygun olsa bile): 409.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Company] = SharedScope.Team }))).StatusCode);

        // Tavanı olmayan türde geniş kapsam serbest; tavanlı türde varsayılan geniş olsa da istisna tavanın içindeyse geçerli.
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/roles", Request(SharedScope.All, new() { [Company] = SharedScope.Own }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Contact] = SharedScope.All }))).StatusCode);

        // Güncelleme: değişmeyen kapsam kilitlenmez (yalnızca ad), değişen ve tavanı aşan 409.
        var rename = await client.PutAsJsonAsync($"/api/roles/{wideRole.Id}",
            new SaveRoleRequest(wideRole.Name + "-2", [], null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var widen = await client.PutAsJsonAsync($"/api/roles/{wideRole.Id}",
            new SaveRoleRequest(wideRole.Name + "-2", [], null, null, null, new Dictionary<string, SharedScope> { [Company] = SharedScope.Unit }));
        Assert.Equal(HttpStatusCode.Conflict, widen.StatusCode);

        // Tavan kalkınca aynı istek geçer.
        await SetLimitAsync(platform, tenant.Id, null, null);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/roles/{wideRole.Id}",
            new SaveRoleRequest(wideRole.Name + "-2", [], null, null, null, new Dictionary<string, SharedScope> { [Company] = SharedScope.Unit }))).StatusCode);
    }

    [Fact]
    public async Task Scope_types_list_the_limits_of_the_own_tenant_only()
    {
        var a = await SetUpTenantAsync();
        var b = await SetUpTenantAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await SetLimitAsync(platform, a.Tenant.Id, Company, SharedScope.Team);

        using var clientA = await LoginAsync(a.Admin);
        using var clientB = await LoginAsync(b.Admin);
        var typesA = (await clientA.GetFromJsonAsync<List<RoleScopeTypeDto>>("/api/roles/scope-types"))!;
        var typesB = (await clientB.GetFromJsonAsync<List<RoleScopeTypeDto>>("/api/roles/scope-types"))!;

        Assert.Equal(SharedScope.Team, typesA.Single(t => t.EntityKey == Company).MaxScope);
        Assert.Null(typesA.Single(t => t.EntityKey == Contact).MaxScope);
        Assert.All(typesB, t => Assert.Null(t.MaxScope));

        // Alt kayıt türleri ve kapalı modüllerin türleri listede yok; liste koda sabit değil, kayıt depolarından gelir.
        Assert.DoesNotContain(typesA, t => t.EntityKey == "crm.company-contact");
        Assert.DoesNotContain(typesA, t => t.ModuleKey == "marketing");

        // B firmasının rolü A'nın tavanından etkilenmez.
        Assert.Equal(HttpStatusCode.Created, (await clientB.PostAsJsonAsync("/api/roles", Request(SharedScope.All))).StatusCode);
    }

    [Fact]
    public async Task Role_managers_cannot_grant_wider_overrides_than_their_own_scope_and_others_cannot_edit_roles()
    {
        var (tenant, _) = await SetUpTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CoreAccess.ManageRoles], Scope.Own, manager);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own, reader);

        using var managerClient = await LoginAsync(manager);
        using var readerClient = await LoginAsync(reader);

        // Kendi kapsamı "yalnızca kendi": istisna da en fazla bu kadar olabilir.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await managerClient.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Company] = SharedScope.All }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await managerClient.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Company] = SharedScope.Own }))).StatusCode);

        // Rol yönetme izni olmayan ne yazabilir ne tavan listesini okuyabilir.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await readerClient.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Company] = SharedScope.Own }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/roles/scope-types")).StatusCode);
    }

    [Fact]
    public async Task Override_changes_are_audited_and_removed_with_the_role()
    {
        var (tenant, admin) = await SetUpTenantAsync();
        using var client = await LoginAsync(admin);

        var created = await client.PostAsJsonAsync("/api/roles", Request(SharedScope.Own, new() { [Company] = SharedScope.All }));
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/roles/{role.Id}",
            new SaveRoleRequest(role.Name, [], null, null, null, new Dictionary<string, SharedScope> { [Company] = SharedScope.Team }))).StatusCode);

        var audit = await client.GetStringAsync($"/api/audit?entityKey=core.role");
        Assert.Contains("scopeOverrides", audit);
        Assert.Contains("crm.company", audit);

        const string count = "SELECT COUNT(*) FROM [core].[RoleScopeOverrides] WHERE [RoleId] = @r";
        Assert.Equal(1, await factory.ScalarAsync<int>(tenant.Id, count, ("@r", role.Id)));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/roles/{role.Id}")).StatusCode);
        Assert.Equal(0, await factory.ScalarAsync<int>(tenant.Id, count, ("@r", role.Id)));
    }

    private static SaveRoleRequest Request(
        SharedScope scope, Dictionary<string, SharedScope>? overrides = null, string[]? permissions = null) =>
        new($"Rol {Guid.NewGuid():N}"[..20], permissions ?? [], scope, null, null, overrides);

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> SetUpTenantAsync()
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

    private async Task AssignAsync(Core.Platform.Tenant tenant, ModulixUser user, Guid roleId)
    {
        using var scope = factory.OpenScope(tenant.Id);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenant.Id), [roleId], CancellationToken.None));
    }

    private static async Task CreateCompanyAsync(HttpClient client, string name) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name))).StatusCode);

    private static async Task<int> CountCompaniesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=200"))!.TotalCount;

    private static async Task SetLimitAsync(HttpClient platform, Guid tenantId, string? entityKey, SharedScope? max)
    {
        ScopeLimitDto[] limits = entityKey is null || max is null ? [] : [new ScopeLimitDto(entityKey, max.Value)];
        Assert.Equal(HttpStatusCode.OK,
            (await platform.PutAsJsonAsync($"/api/platform/tenants/{tenantId}/scope-limits", new SetScopeLimitsRequest(limits))).StatusCode);
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
