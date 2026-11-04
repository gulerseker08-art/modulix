using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim A: sektör/faaliyet kataloğu platform geneli, tenant seçimi RLS altındadır. Tablo kapısı (TableInventoryTests)
/// sınıflandırmayı, bu testler izolasyonu ve katalog bütünlüğünü doğrular.
/// </summary>
public class SectorActivityCatalogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Tenant_selection_is_saved_and_read_back()
    {
        var tenant = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(tenant);

        using (var scope = factory.OpenScope(tenant))
        {
            Db(scope).ActivitySelections.Add(new TenantActivitySelection(node));
            await Db(scope).SaveChangesAsync();
        }

        using var read = factory.OpenScope(tenant);
        var selection = Assert.Single(await Db(read).ActivitySelections.Where(s => s.NodeId == node).ToListAsync());
        Assert.Equal(tenant, selection.TenantId);
    }

    [Fact]
    public async Task Same_node_twice_in_one_tenant_is_rejected_but_two_tenants_may_select_it()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(a);

        await SelectAsync(a, node);
        await SelectAsync(b, node);

        await Assert.ThrowsAsync<DbUpdateException>(() => SelectAsync(a, node));
    }

    [Fact]
    public async Task One_tenant_cannot_see_another_tenants_selection_even_without_query_filter()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(a);
        await SelectAsync(a, node);
        await SelectAsync(b, node);

        using var scope = factory.OpenScope(a);
        var filtered = await Db(scope).ActivitySelections.Where(s => s.NodeId == node).ToListAsync();
        var unfiltered = await Db(scope).ActivitySelections.IgnoreQueryFilters().Where(s => s.NodeId == node).ToListAsync();

        Assert.Equal(a, Assert.Single(filtered).TenantId);
        Assert.Equal(a, Assert.Single(unfiltered).TenantId);
    }

    [Fact]
    public async Task Without_active_tenant_no_selection_is_visible_but_the_catalog_is()
    {
        var tenant = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(tenant);
        await SelectAsync(tenant, node);

        using var scope = factory.OpenScope(tenantId: null);
        Assert.Empty(await Db(scope).ActivitySelections.IgnoreQueryFilters().Where(s => s.NodeId == node).ToListAsync());
        Assert.True(await Db(scope).ActivityNodes.AnyAsync(n => n.Id == node), "Katalog platform geneli okunabilir olmalı.");
    }

    [Fact]
    public async Task Catalog_code_is_unique()
    {
        var tenant = (await factory.CreateTenantAsync()).Id;
        var code = Code();
        await AddNodeAsync(tenant, code);

        await Assert.ThrowsAsync<DbUpdateException>(() => AddNodeAsync(tenant, code));
    }

    [Fact]
    public async Task Concurrent_admin_edit_does_not_silently_overwrite()
    {
        var tenant = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(tenant);

        using var first = factory.OpenScope(tenant);
        using var second = factory.OpenScope(tenant);
        var a = await Db(first).ActivityNodes.SingleAsync(n => n.Id == node);
        var b = await Db(second).ActivityNodes.SingleAsync(n => n.Id == node);

        a.Describe("Birinci ad", null);
        await Db(first).SaveChangesAsync();

        b.Describe("İkinci ad", null);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Db(second).SaveChangesAsync());
    }

    [Fact]
    public async Task Deactivating_a_node_keeps_the_row_and_existing_selections()
    {
        var tenant = (await factory.CreateTenantAsync()).Id;
        var node = await AddNodeAsync(tenant);
        await SelectAsync(tenant, node);

        using (var scope = factory.OpenScope(tenant))
        {
            (await Db(scope).ActivityNodes.SingleAsync(n => n.Id == node)).SetActive(false);
            await Db(scope).SaveChangesAsync();
        }

        using var read = factory.OpenScope(tenant);
        Assert.False((await Db(read).ActivityNodes.SingleAsync(n => n.Id == node)).IsActive);
        Assert.Single(await Db(read).ActivitySelections.Where(s => s.NodeId == node).ToListAsync());
    }

    private async Task<Guid> AddNodeAsync(Guid tenant, string? code = null)
    {
        using var scope = factory.OpenScope(tenant);
        var node = new ActivityNode(code ?? Code(), null, ActivityNodeKind.Sector, "Test sektörü");
        Db(scope).ActivityNodes.Add(node);
        await Db(scope).SaveChangesAsync();
        return node.Id;
    }

    private async Task SelectAsync(Guid tenant, Guid node)
    {
        using var scope = factory.OpenScope(tenant);
        Db(scope).ActivitySelections.Add(new TenantActivitySelection(node));
        await Db(scope).SaveChangesAsync();
    }

    private static ModulixDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

    private static string Code() => $"t-{Guid.NewGuid():N}";
}
