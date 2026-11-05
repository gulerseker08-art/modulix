using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Sectors;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim A: sektör/faaliyet kataloğu yönetimi (platform) ve tenant faaliyet seçimi. Katalog global tabloda, kodlar
/// benzersizdir; testler birbirini etkilemesin diye her test kendi rastgele kodlarıyla çalışır.
/// </summary>
public class SectorCatalogApiTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Platform_administrator_builds_a_tree_and_tenant_selects_an_activity()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var (sector, group, activity) = await CreateTreeAsync(platform);

        var capability = $"c-{Guid.NewGuid():N}"[..14];
        Assert.Equal(HttpStatusCode.Created, (await platform.PostAsJsonAsync("/api/platform/sectors/capabilities", new CreateCapabilityRequest(capability, "Test yeteneği"))).StatusCode);
        var withCapability = await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{activity.Id}/capabilities", new CodeListRequest([capability]));
        Assert.Equal(HttpStatusCode.OK, withCapability.StatusCode);
        Assert.Equal([capability], (await withCapability.Content.ReadFromJsonAsync<ActivityNodeDto>())!.Capabilities);

        var (tenant, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);

        var catalog = (await client.GetFromJsonAsync<SectorCatalogResponse>("/api/sectors/catalog"))!;
        Assert.Contains(catalog.Nodes, n => n.Id == activity.Id && n.ParentId == group.Id);

        // Boş seçim geçerlidir (eski tenant'lar değişmez).
        Assert.Empty((await client.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);

        var saved = await client.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var selection = (await saved.Content.ReadFromJsonAsync<TenantActivitySelectionResponse>())!;
        Assert.Equal([activity.Id], selection.SelectedNodeIds);

        // Üst bağlam türetilir; üst başlığı seçmek alt faaliyeti seçmiş saymaz.
        Assert.Equal([sector.Id, group.Id], selection.Preview.ContextNodeIds.Order());
        Assert.DoesNotContain(group.Id, selection.SelectedNodeIds);
        Assert.Equal([activity.Id], selection.Preview.CatalogOnlyNodeIds);

        // Kayıt tenant'ın denetim kaydında, katalog değişikliği platform denetim kaydındadır.
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.activity-selection").ToListAsync();
            var entry = Assert.Single(audit);
            Assert.Equal(AuditAction.Updated, entry.Action);
            Assert.Equal(admin.Id, entry.UserId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var actions = await db.PlatformAuditLog.AsNoTracking()
                .Where(a => a.ActorUserId == platformAdmin.Id).Select(a => a.Action).ToListAsync();
            Assert.Contains("sector.node.created", actions);
            Assert.Contains("sector.capability.created", actions);
            Assert.Contains("sector.node.capabilities", actions);
        }
    }

    [Fact]
    public async Task Invalid_catalog_changes_and_selections_are_rejected_without_side_effects()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var (sector, group, activity) = await CreateTreeAsync(platform);

        // Kod biçimi, kök kuralı, tekrar eden kod.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest("Gecersiz Kod", sector.Id, "Group", "Ad", null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(NewCode(), null, "Activity", "Köksüz faaliyet", null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(group.Code, sector.Id, "Group", "Aynı kod", null, 0))).StatusCode);

        // Aktif alt düğümü olan grup pasife alınamaz; önce alt düğüm pasife alınır.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{group.Id}/active", new SetActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{activity.Id}/active", new SetActiveRequest(false))).StatusCode);

        // Eşzamanlı düzenleme: eski sürümle yazılan güncelleme çakışma olarak reddedilir.
        var current = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors"))!.Nodes.Single(n => n.Id == group.Id);
        var first = await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{group.Id}",
            new UpdateActivityNodeRequest("Yeni ad", null, 5, "Available", current.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stale = await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{group.Id}",
            new UpdateActivityNodeRequest("Eski sürüm", null, 5, "Available", current.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Yeni ad", (await first.Content.ReadFromJsonAsync<ActivityNodeDto>())!.Name);

        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{Guid.NewGuid()}/active", new SetActiveRequest(true))).StatusCode);

        // Yetenek: bilinmeyen kod, kendine bağlanma ve döngü.
        var a = $"a-{Guid.NewGuid():N}"[..14];
        var b = $"b-{Guid.NewGuid():N}"[..14];
        foreach (var code in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.Created, (await platform.PostAsJsonAsync("/api/platform/sectors/capabilities", new CreateCapabilityRequest(code, code))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{group.Id}/capabilities", new CodeListRequest(["yok-boyle-yetenek"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/platform/sectors/capabilities/{a}/dependencies", new CodeListRequest([a]))).StatusCode);
        var depends = await platform.PutAsJsonAsync($"/api/platform/sectors/capabilities/{a}/dependencies", new CodeListRequest([b]));
        Assert.True(depends.IsSuccessStatusCode, await depends.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/platform/sectors/capabilities/{b}/dependencies", new CodeListRequest([a]))).StatusCode);

        // Tenant: pasif ve bilinmeyen faaliyet seçilemez; reddedilen seçim hiçbir şeyi değiştirmez.
        var (tenant, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([Guid.NewGuid()]))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);

        var preview = (await (await client.PostAsJsonAsync("/api/sectors/selection/preview", new ActivityIdsRequest([activity.Id]))).Content.ReadFromJsonAsync<SelectionPreviewDto>())!;
        Assert.False(preview.IsComplete);
        Assert.Single(preview.Errors);

        using var scope = factory.OpenScope(tenant.Id);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(x => x.EntityKey == "core.activity-selection").ToListAsync());
    }

    [Fact]
    public async Task One_tenant_cannot_see_or_change_another_tenants_selection()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var (_, _, activity) = await CreateTreeAsync(platform);

        var (tenantA, adminA) = await CreateTenantAdminAsync();
        var (tenantB, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).StatusCode);

        // B aynı faaliyeti seçebilir (katalog ortaktır) ama A'nın seçimini görmez; B boş seçim kaydedince A etkilenmez.
        Assert.Empty((await clientB.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);
        Assert.Equal(HttpStatusCode.OK, (await clientB.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([]))).StatusCode);

        Assert.Equal([activity.Id], (await clientA.GetFromJsonAsync<TenantActivitySelectionResponse>("/api/sectors/selection"))!.SelectedNodeIds);
        using var scope = factory.OpenScope(tenantA.Id);
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().ActivitySelections.AsNoTracking().ToListAsync());
        Assert.NotEqual(tenantA.Id, tenantB.Id);
    }

    [Fact]
    public async Task Only_authorized_users_reach_catalog_management_and_tenant_selection()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var (_, _, activity) = await CreateTreeAsync(platform);

        // Firma yöneticisi platform katalogunu yönetemez; oturumsuz istek 401.
        var (tenant, admin) = await CreateTenantAdminAsync();
        using var adminClient = await LoginAsync(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/platform/sectors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(NewCode(), null, "Sector", "İzinsiz", null, 0))).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sectors/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/sectors")).StatusCode);

        // Firmada özelleştirme izni olmayan kullanıcı seçimi göremez ve değiştiremez.
        var rep = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [], rep);
        using var repClient = await LoginAsync(rep);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/sectors/selection")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).StatusCode);

        // Platform yöneticisi firma bağlamı olmadan tenant seçim uçlarına giremez (firma verisi platformdan görülmez).
        Assert.False((await platform.GetAsync("/api/sectors/selection")).IsSuccessStatusCode);
        Assert.False((await platform.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).IsSuccessStatusCode);
    }

    private static string NewCode() => $"t-{Guid.NewGuid():N}"[..14];

    private static async Task<(ActivityNodeDto Sector, ActivityNodeDto Group, ActivityNodeDto Activity)> CreateTreeAsync(HttpClient platform)
    {
        var sector = await CreateNodeAsync(platform, null, "Sector", "Test sektörü");
        var group = await CreateNodeAsync(platform, sector.Id, "Group", "Test grubu");
        var activity = await CreateNodeAsync(platform, group.Id, "Activity", "Test faaliyeti");
        return (sector, group, activity);
    }

    private static async Task<ActivityNodeDto> CreateNodeAsync(HttpClient platform, Guid? parentId, string kind, string name)
    {
        var response = await platform.PostAsJsonAsync("/api/platform/sectors/nodes", new CreateActivityNodeRequest(NewCode(), parentId, kind, name, null, 0));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActivityNodeDto>())!;
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
