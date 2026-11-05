using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0030 bölüm 8: tavan kümesi kaydedilmeden önce etkisi önizlenir (<c>POST .../scope-limits/preview</c>). Önizleme hiçbir şey yazmaz,
/// kaydetmenin sonucuyla örtüşür (kısılan üye, bugünkü ve önerilen tavan farkı), yöneticiyi yalnızca ona bağlanan tavanda sayar,
/// kaydedilirkenki doğrulamayı uygular, yalnızca platform yöneticisine açıktır ve başka firmanın üyelerini saymaz.
/// </summary>
public sealed class ScopeLimitPreviewTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Company = "crm.company";

    [Fact]
    public async Task Preview_counts_who_gets_restricted_without_writing_and_matches_applying()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var viewer = await LoginAsync(setup.Viewer);
        var own = new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]);

        // Görüntüleyici (kapsamı tümü) kısılır; iki temsilci zaten kendi kayıtlarını görür; yönetici muaftır.
        var preview = await PreviewAsync(platform, setup.Tenant.Id, own);
        Assert.Equal(1, preview.AffectedRoleCount);
        Assert.Equal(1, preview.NewlyRestrictedMemberCount);
        Assert.Equal(0, preview.ReleasedMemberCount);
        Assert.Equal(0, preview.NewlyRestrictedAdministratorCount);
        var row = Assert.Single(preview.Types);
        Assert.Equal((Company, 1, 1), (row.EntityKey, row.AffectedRoles, row.NewlyRestrictedMembers));

        // Önizleme yazmaz: tavan yok, denetim kaydı yok, görüntüleyici hâlâ üç firmayı görür.
        Assert.Empty((await platform.GetFromJsonAsync<TenantScopeLimitsResponse>(Url(setup.Tenant.Id)))!.Limits);
        Assert.Empty((await platform.GetFromJsonAsync<PlatformAuditPageResponse>(
            $"/api/platform/audit?tenantId={setup.Tenant.Id}&action=tenant.scope-limits-changed"))!.Items);
        Assert.Equal(3, await CountCompaniesAsync(viewer));

        // Kaydedince önizlemenin söylediği olur; aynı küme yeniden önizlenince yeni kısılan kimse yoktur, rol sayısı sürer.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(Url(setup.Tenant.Id), own)).StatusCode);
        Assert.Equal(0, await CountCompaniesAsync(viewer));
        var again = await PreviewAsync(platform, setup.Tenant.Id, own);
        Assert.Equal((1, 0, 0), (again.AffectedRoleCount, again.NewlyRestrictedMemberCount, again.ReleasedMemberCount));

        // Tavanı kaldırmak üyeyi serbest bırakır.
        var lift = await PreviewAsync(platform, setup.Tenant.Id, new SetScopeLimitsRequest([]));
        Assert.Equal((0, 0, 1), (lift.AffectedRoleCount, lift.NewlyRestrictedMemberCount, lift.ReleasedMemberCount));
        Assert.Equal(1, Assert.Single(lift.Types).ReleasedMembers);
    }

    [Fact]
    public async Task Preview_counts_administrators_only_when_the_limit_binds_them()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());

        var exempt = await PreviewAsync(platform, setup.Tenant.Id,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]));
        Assert.Equal(0, exempt.NewlyRestrictedAdministratorCount);

        var bound = await PreviewAsync(platform, setup.Tenant.Id,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own, AppliesToAdministrators: true)]));
        Assert.Equal(1, bound.NewlyRestrictedAdministratorCount);
        Assert.Equal(1, bound.NewlyRestrictedMemberCount);
        Assert.Equal(1, Assert.Single(bound.Types).NewlyRestrictedAdministrators);

        // Yöneticiyi bağlayan tavan kaydedilip gevşetilince yönetici serbest kalır, üye sayısı etkilenmez.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(Url(setup.Tenant.Id),
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own, AppliesToAdministrators: true)]))).StatusCode);
        var relaxed = await PreviewAsync(platform, setup.Tenant.Id,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]));
        Assert.Equal((1, 0), (relaxed.ReleasedAdministratorCount, relaxed.NewlyRestrictedAdministratorCount));
        Assert.Equal(0, relaxed.ReleasedMemberCount);
    }

    [Fact]
    public async Task Preview_validates_like_the_write_is_platform_only_and_ignores_other_tenants()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var tenantAdmin = await LoginAsync(a.Admin);
        var own = new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]);

        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(PreviewUrl(a.Tenant.Id),
            new SetScopeLimitsRequest([new ScopeLimitDto("crm.nothing", SharedScope.Own)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(PreviewUrl(a.Tenant.Id),
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own), new ScopeLimitDto(Company, SharedScope.Team)]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync(PreviewUrl(Guid.NewGuid()), own)).StatusCode);

        // Firmanın kendi yöneticisi platform ucuna giremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PostAsJsonAsync(PreviewUrl(a.Tenant.Id), own)).StatusCode);

        // İki firmada da birer geniş kapsamlı görüntüleyici var; her önizleme yalnızca kendi firmasını sayar.
        Assert.Equal(1, (await PreviewAsync(platform, a.Tenant.Id, own)).NewlyRestrictedMemberCount);
        Assert.Equal(1, (await PreviewAsync(platform, b.Tenant.Id, own)).NewlyRestrictedMemberCount);
    }

    private static string Url(Guid tenantId) => $"/api/platform/tenants/{tenantId}/scope-limits";

    private static string PreviewUrl(Guid tenantId) => $"{Url(tenantId)}/preview";

    private static async Task<ScopeLimitsPreviewResponse> PreviewAsync(HttpClient platform, Guid tenantId, SetScopeLimitsRequest request)
    {
        var response = await platform.PostAsJsonAsync(PreviewUrl(tenantId), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ScopeLimitsPreviewResponse>())!;
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Viewer)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var rep1 = await factory.CreateUserAsync(tenant);
        var rep2 = await factory.CreateUserAsync(tenant);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] access = [CompaniesRead, CompaniesWrite, ContactsRead];
        await factory.CreateRoleAsync(tenant.Id, access, Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, access, Scope.Own, rep1, rep2);

        // Üç firma, üç farklı sorumlu (görüntüleyicinin kendi kaydı yok).
        foreach (var (user, name) in new[] { (rep1, "Firma 1"), (rep2, "Firma 2"), (rep1, "Firma 3") })
        {
            using var client = await LoginAsync(user);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name))).StatusCode);
        }

        return (tenant, admin, viewer);
    }

    private static async Task<int> CountCompaniesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=200"))!.TotalCount;

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
