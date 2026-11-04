using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Sectors;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim D: lojistik başlangıç paketi. Katalog global ve kalıcıdır; kurulum yalnızca ekler ve tekrar çalıştırılabilir,
/// bu yüzden testler "ilk kurulum mu" varsaymaz, sonuç durumunu doğrular.
/// </summary>
public class StarterPackageTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Logistics = "logistics";

    [Fact]
    public async Task Platform_installs_the_unverified_logistics_package_and_a_tenant_can_select_from_it()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());

        var listed = (await platform.GetFromJsonAsync<List<StarterPackageDto>>("/api/platform/sectors/starter-packages"))!;
        var package = Assert.Single(listed, p => p.Code == Logistics);
        Assert.False(package.Verified); // doğrulanmamış öneri açıkça işaretli
        Assert.Contains("doğrulanmadı", package.VerificationNote, StringComparison.OrdinalIgnoreCase);

        var install = await platform.PostAsJsonAsync($"/api/platform/sectors/starter-packages/{Logistics}/install", new { });
        Assert.True(install.IsSuccessStatusCode, await install.Content.ReadAsStringAsync());

        var catalog = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors"))!;
        var byCode = catalog.Nodes.ToDictionary(n => n.Code);
        Assert.Equal("Sector", byCode["lojistik"].Kind);
        Assert.Null(byCode["lojistik"].ParentId);
        Assert.Equal(byCode["tasimacilik"].Id, byCode["karayolu-tasimaciligi"].ParentId);
        Assert.Equal(byCode["depolama"].Id, byCode["gumruklu-antrepo"].ParentId);
        Assert.Equal(["logistics.carriers", "logistics.shipments"], byCode["karayolu-tasimaciligi"].Capabilities);
        Assert.Equal(["warehouse.bonded"], byCode["gumruklu-antrepo"].Capabilities);

        // Faaliyet kataloğu operasyon modülünün hazır olduğunu söylemez (K5): hiçbiri Available değildir.
        var installed = package.NodeCount;
        Assert.Equal(installed, catalog.Nodes.Count(n => n.Code is "lojistik" or "tasimacilik" or "karayolu-tasimaciligi" or "denizyolu-tasimaciligi"
            or "havayolu-tasimaciligi" or "demiryolu-tasimaciligi" or "depolama" or "gumruklu-antrepo" or "serbest-depolama"
            or "dagitim-son-kilometre" or "tasima-organizasyonu" or "transit-aktarma" or "diger-lojistik"));
        Assert.All(catalog.Nodes.Where(n => n.Code is "lojistik" or "karayolu-tasimaciligi" or "gumruklu-antrepo"), n => Assert.Equal("CatalogOnly", n.Readiness));
        Assert.Equal(["warehouse.stock"], catalog.Capabilities.Single(c => c.Code == "warehouse.bonded").DependsOn);

        // Tekrar kurulum hiçbir şey eklemez.
        var again = (await (await platform.PostAsJsonAsync($"/api/platform/sectors/starter-packages/{Logistics}/install", new { })).Content
            .ReadFromJsonAsync<StarterPackagePlanDto>())!;
        Assert.True(again.NothingToDo);
        Assert.Empty(again.NodesToCreate);

        // Firma kurulan bir faaliyeti seçebilir; üst bağlam türetilir, başka firmanın seçimi etkilenmez.
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var saved = await clientA.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([byCode["gumruklu-antrepo"].Id]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var selection = (await saved.Content.ReadFromJsonAsync<TenantActivitySelectionResponse>())!;
        Assert.Equal([byCode["lojistik"].Id, byCode["depolama"].Id], selection.Preview.ContextNodeIds.Order());
        Assert.Empty((await clientB.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);
    }

    [Fact]
    public async Task Install_only_adds_and_never_overwrites_what_the_platform_changed_and_is_audited()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(admin);
        await platform.PostAsJsonAsync($"/api/platform/sectors/starter-packages/{Logistics}/install", new { });

        // Yönetici bir düğümün adını ve hazırlık durumunu değiştirir; yeniden kurulum bunları ezmez.
        var node = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors"))!.Nodes.Single(n => n.Code == "serbest-depolama");
        var renamed = $"Serbest depolama {Guid.NewGuid():N}"[..30];
        var update = await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{node.Id}",
            new UpdateActivityNodeRequest(renamed, node.Description, node.SortOrder, "Available", node.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var plan = (await (await platform.PostAsJsonAsync($"/api/platform/sectors/starter-packages/{Logistics}/install", new { })).Content
            .ReadFromJsonAsync<StarterPackagePlanDto>())!;
        Assert.Contains("serbest-depolama", plan.NodesExisting);
        Assert.DoesNotContain("serbest-depolama", plan.NodesToCreate);

        var after = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors"))!.Nodes.Single(n => n.Code == "serbest-depolama");
        Assert.Equal(renamed, after.Name);
        Assert.Equal("Available", after.Readiness);

        // Kurulumun yazdığı her kayıt, kuran yönetici ile platform denetim kaydındadır.
        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().PlatformAuditLog.AsNoTracking()
            .Where(a => a.ActorUserId == admin.Id).Select(a => a.Action).ToListAsync();
        Assert.Contains("sector.node.updated", actions);
    }

    [Fact]
    public async Task Unknown_package_and_preview_without_writing_are_handled()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync("/api/platform/sectors/starter-packages/yok/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/yok/install", new { })).StatusCode);

        var preview = await platform.GetAsync($"/api/platform/sectors/starter-packages/{Logistics}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        // Önizleme yalnızca plan döner. (Katalog sayısı karşılaştırılmaz: diğer testler aynı global kataloğa paralel yazar.)
        var plan = (await preview.Content.ReadFromJsonAsync<StarterPackagePlanDto>())!;
        Assert.Equal(plan.NodesToCreate.Count == 0 && plan.CapabilitiesToCreate.Count == 0, plan.NothingToDo);
    }

    [Fact]
    public async Task Tenant_administrators_and_anonymous_callers_cannot_manage_starter_packages()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var tenant = await LoginAsync(admin);
        using var anonymous = factory.CreateBrowserClient();

        foreach (var client in new[] { tenant, anonymous })
        {
            var status = (await client.GetAsync("/api/platform/sectors/starter-packages")).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"liste: {status}");
            status = (await client.PostAsJsonAsync($"/api/platform/sectors/starter-packages/{Logistics}/install", new { })).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"kurulum: {status}");
        }
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> CreateTenantAdminAsync()
    {
        var tenant = await factory.CreateTenantAsync();
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
