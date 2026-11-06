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
/// ADR-0029 Dilim G: başlangıç paketinin geri alınması. Geri alma düğümleri siler değil pasife alır; tenant seçimleri ve kayıtlardaki kodlar korunur.
/// Katalog global olduğundan her test sonunda düğümleri yeniden açar (diğer testler etkin düğüm bekler).
/// </summary>
[Collection("SectorCatalog")]
public class StarterPackageRetireTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Logistics = "logistics";
    private const string Base = "/api/platform/sectors";

    [Fact]
    public async Task Retire_preview_writes_nothing_and_retire_deactivates_without_deleting_and_keeps_tenant_selections()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(admin);
        await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/install", new { });
        var (_, tenantAdmin) = await CreateTenantAdminAsync();
        using var tenant = await LoginAsync(tenantAdmin);
        var antrepo = (await Catalog(platform)).Single(n => n.Code == "gumruklu-antrepo");
        Assert.Equal(HttpStatusCode.OK, (await tenant.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([antrepo.Id]))).StatusCode);

        var toRestore = new List<Guid>();
        try
        {
            var preview = (await platform.GetFromJsonAsync<StarterPackageRetirePlanDto>($"{Base}/starter-packages/{Logistics}/retire-preview"))!;
            Assert.Contains("gumruklu-antrepo", preview.NodesToDeactivate);
            Assert.False(preview.NothingToDo);
            Assert.NotEmpty(preview.Warnings);
            // Önizleme yazmaz: düğüm hâlâ etkin.
            Assert.True((await Catalog(platform)).Single(n => n.Code == "gumruklu-antrepo").IsActive);

            var response = await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/retire", new { });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var plan = (await response.Content.ReadFromJsonAsync<StarterPackageRetirePlanDto>())!;
            toRestore = [.. (await Catalog(platform)).Where(n => plan.NodesToDeactivate.Contains(n.Code)).Select(n => n.Id)];

            // Çocuktan ata doğru sıra: alt düğüm üstünden önce, hepsi pasif ve katalogda duruyor (silinmedi).
            var order = plan.NodesToDeactivate.ToList();
            Assert.True(order.IndexOf("gumruklu-antrepo") >= 0 && (order.IndexOf("depolama") < 0 || order.IndexOf("gumruklu-antrepo") < order.IndexOf("depolama")), "alt düğüm üstünden önce gelmeli");
            var after = (await Catalog(platform)).ToDictionary(n => n.Code);
            Assert.All(plan.NodesToDeactivate, code => Assert.False(after[code].IsActive));

            // Tenant seçimi silinmez.
            var selection = (await tenant.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!;
            Assert.Contains(antrepo.Id, selection.SelectedNodeIds);

            // Tekrar çalıştırmak bir şey yapmaz.
            var again = (await (await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/retire", new { })).Content
                .ReadFromJsonAsync<StarterPackageRetirePlanDto>())!;
            Assert.True(again.NothingToDo);
            Assert.Contains("gumruklu-antrepo", again.NodesAlreadyInactive);

            // Geri dönüş: düğümleri yeniden açmak yeterli (ata önce).
            await ReactivateAsync(platform, toRestore);
            toRestore = [];
            Assert.True((await Catalog(platform)).Single(n => n.Code == "gumruklu-antrepo").IsActive);

            using var scope = factory.Services.CreateScope();
            var actions = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().PlatformAuditLog.AsNoTracking()
                .Where(a => a.ActorUserId == admin.Id).Select(a => a.Action).ToListAsync();
            Assert.Contains("sector.node.deactivated", actions);
            Assert.Contains("sector.node.activated", actions);
        }
        finally
        {
            await ReactivateAsync(platform, toRestore);
        }
    }

    [Fact]
    public async Task Unknown_package_is_not_found_and_nodes_with_foreign_active_children_are_skipped()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"{Base}/starter-packages/yok/retire-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"{Base}/starter-packages/yok/retire", new { })).StatusCode);

        await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/install", new { });
        var nodes = await Catalog(platform);
        var parent = nodes.Single(n => n.Code == "depolama");
        var foreign = $"ozel-{Guid.NewGuid():N}"[..14];
        var created = await platform.PostAsJsonAsync($"{Base}/nodes", new CreateActivityNodeRequest(foreign, parent.Id, "Activity", "Özel alt faaliyet", null, 99));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var foreignId = (await created.Content.ReadFromJsonAsync<ActivityNodeDto>())!.Id;

        var toRestore = new List<Guid>();
        try
        {
            var plan = (await (await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/retire", new { })).Content
                .ReadFromJsonAsync<StarterPackageRetirePlanDto>())!;
            toRestore = [.. (await Catalog(platform)).Where(n => plan.NodesToDeactivate.Contains(n.Code)).Select(n => n.Id)];

            // Paket dışı etkin alt düğümü olan düğüm ve atalarına dokunulmaz; paketin diğer yaprakları pasife alınır.
            Assert.True(plan.NodesToDeactivate.Contains("serbest-depolama") || plan.NodesAlreadyInactive.Contains("serbest-depolama"), "paketin diğer yaprağı etkilenir ya da zaten pasiftir");
            Assert.DoesNotContain("depolama", plan.NodesToDeactivate);
            Assert.DoesNotContain("lojistik", plan.NodesToDeactivate);
            Assert.Contains(plan.Skipped, s => s.Contains("'depolama'") && s.Contains(foreign));
            Assert.Contains("gumruklu-antrepo", plan.NodesToDeactivate);

            var after = (await Catalog(platform)).ToDictionary(n => n.Code);
            Assert.True(after["depolama"].IsActive);
            Assert.True(after[foreign].IsActive);
        }
        finally
        {
            await ReactivateAsync(platform, toRestore);
            await platform.PutAsJsonAsync($"{Base}/nodes/{foreignId}/active", new SetActiveRequest(false));
        }
    }

    [Fact]
    public async Task Retire_does_not_touch_one_tenants_selection_through_another_tenant()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/install", new { });
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var karayolu = (await Catalog(platform)).Single(n => n.Code == "karayolu-tasimaciligi");
        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([karayolu.Id]))).StatusCode);

        var toRestore = new List<Guid>();
        try
        {
            var plan = (await (await platform.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/retire", new { })).Content
                .ReadFromJsonAsync<StarterPackageRetirePlanDto>())!;
            toRestore = [.. (await Catalog(platform)).Where(n => plan.NodesToDeactivate.Contains(n.Code)).Select(n => n.Id)];

            // Platform geri alması hiçbir firmanın seçimini silmez ve boş seçimli firmaya seçim yazmaz.
            Assert.Contains(karayolu.Id, (await clientA.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);
            Assert.Empty((await clientB.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);

            // Pasif faaliyet yeni seçilemez.
            var blocked = await clientB.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([karayolu.Id]));
            Assert.True(blocked.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict, blocked.StatusCode.ToString());
        }
        finally
        {
            await ReactivateAsync(platform, toRestore);
        }
    }

    [Fact]
    public async Task Tenant_administrators_and_anonymous_callers_cannot_preview_or_retire_starter_packages()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var tenant = await LoginAsync(admin);
        using var anonymous = factory.CreateBrowserClient();

        foreach (var client in new[] { tenant, anonymous })
        {
            var status = (await client.GetAsync($"{Base}/starter-packages/{Logistics}/retire-preview")).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"önizleme: {status}");
            status = (await client.PostAsJsonAsync($"{Base}/starter-packages/{Logistics}/retire", new { })).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"geri alma: {status}");
        }
    }

    private static async Task<List<ActivityNodeDto>> Catalog(HttpClient platform) =>
        (await platform.GetFromJsonAsync<SectorCatalogResponse>(Base))!.Nodes;

    // Geri açma ata önce yapılır: düğümler katalogdaki derinliğe göre sıralanır (sıra varsayımına güvenilmez).
    private static async Task ReactivateAsync(HttpClient platform, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var nodes = await Catalog(platform);
        var byId = nodes.ToDictionary(n => n.Id);
        int Depth(ActivityNodeDto n) => n.ParentId is { } p && byId.TryGetValue(p, out var parent) ? 1 + Depth(parent) : 0;
        foreach (var node in ids.Where(byId.ContainsKey).Select(id => byId[id]).OrderBy(Depth))
        {
            await platform.PutAsJsonAsync($"{Base}/nodes/{node.Id}/active", new SetActiveRequest(true));
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
