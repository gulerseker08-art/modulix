using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Lookups;
using Modulix.Shared.Records;
using Modulix.Shared.Warehouse;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 6b (Partiler, Stok hareketleri, Stok bakiyeleri ekranları): ekranların dayandığı sunucu sözleşmesi. Mutlu yol (başlığa göre
/// sıralama, parti/lokasyon/stok kartı seçim kutuları ve ad çözümleme), doğrulama (sıralanamayan anahtar reddi), tenant izolasyonu (B, A'nın
/// partisini seçim kutusunda bulamaz ve adını çözemez) ve yetki (parti okuma izni olmayan kişiye parti adı sızmaz).
/// </summary>
public class WarehouseStockScreenTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Lots = "/api/records/warehouse.lot";
    private const string Movements = "/api/records/warehouse.stock-movement";
    private const string Balances = "/api/records/warehouse.stock-balance";

    [Fact]
    public async Task Lots_movements_and_balances_sort_by_their_columns_and_lookups_find_lots_locations_and_items()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin, "ALFA");
        var second = await PostAsync(admin, Lots, new JsonObject { ["lotNo"] = "zeta-2", ["stockItemId"] = w.StockItem.ToString(), ["ownerPartyId"] = w.Owner.ToString() });
        await PostAsync(admin, Movements, Move(w.Lot, w.A, 5m));
        await PostAsync(admin, Movements, Move(second.Id, w.A, 20m));
        await PostAsync(admin, Movements, Move(w.Lot, w.B, 1.5m));

        // Partiler: parti numarasına göre iki yönde.
        var lotNos = await ValuesAsync(admin, Lots, "lotNo", "sort=-lotNo");
        Assert.Equal(lotNos.OrderByDescending(n => n, StringComparer.Ordinal), lotNos);
        Assert.Equal("ZETA-2", lotNos[0]);

        // Hareketler: miktara göre; varsayılan en yeni önce.
        Assert.Equal([1.5m, 5m, 20m], await QuantitiesAsync(admin, Movements, "sort=quantity"));
        Assert.Equal([1.5m, 20m, 5m], await QuantitiesAsync(admin, Movements, null));

        // Bakiyeler: en çok stok önce; sıfırlar "nonZero" ile gizlenir.
        Assert.Equal([20m, 5m, 1.5m], await QuantitiesAsync(admin, Balances, "sort=-quantity&nonZero=true"));

        // Seçim kutuları: parti numarasıyla parti, kodla lokasyon, kodla stok kartı bulunur; çözümleme ekrandaki adları verir.
        var lotHit = Assert.Single((await SearchAsync(admin, WarehouseAccess.MovementLotLookup, "zeta")).Items);
        Assert.Equal((second.Id, "ZETA-2"), (lotHit.Id, lotHit.Label));
        Assert.Contains((await SearchAsync(admin, WarehouseAccess.MovementToLocationLookup, null)).Items, i => i.Id == w.A);
        Assert.Contains((await SearchAsync(admin, WarehouseAccess.LotStockItemLookup, "alfa")).Items, i => i.Id == w.StockItem);
        var resolved = await ResolveAsync(admin, WarehouseAccess.MovementFromLocationLookup, [w.A, w.B]);
        Assert.Equal(2, resolved.Count(i => i.Label is { Length: > 0 }));
    }

    [Fact]
    public async Task Unsortable_columns_are_rejected_on_lots_movements_and_balances()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        foreach (var (url, key) in new[]
        {
            // Firma ve stok kartı adına göre sıralama yok (görünürlük sıralamayla dolanılmasın); bakiye yalnızca miktara göre sıralanır.
            (Lots, "ownerPartyId"), (Lots, "stockItemId"), (Lots, "description"),
            (Movements, "lotId"), (Movements, "documentReference"),
            (Balances, "lotId"), (Balances, "locationId"),
        })
        {
            var response = await admin.GetAsync($"{url}?sort={Uri.EscapeDataString(key)}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("sort", (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        }

        foreach (var url in new[] { $"{Lots}?sort=-isActive", $"{Lots}?sort=createdAt", $"{Movements}?sort=-typeKey", $"{Movements}?sort=occurredAt", $"{Balances}?sort=quantity" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_find_or_resolve_a_tenants_lots_and_locations()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var wA = await WorldAsync(adminA, "GIZLI");

        Assert.DoesNotContain((await SearchAsync(adminB, WarehouseAccess.MovementLotLookup, null)).Items, i => i.Id == wA.Lot);
        Assert.DoesNotContain((await SearchAsync(adminB, WarehouseAccess.MovementToLocationLookup, null)).Items, i => i.Id == wA.A);
        Assert.DoesNotContain(await ResolveAsync(adminB, WarehouseAccess.MovementLotLookup, [wA.Lot]), i => i.Label is { Length: > 0 });
        Assert.DoesNotContain(await ResolveAsync(adminB, WarehouseAccess.LotStockItemLookup, [wA.StockItem]), i => i.Label is { Length: > 0 });
    }

    [Fact]
    public async Task Lot_names_need_the_lot_read_permission_even_for_a_movement_reader()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.MovementsReader);
        var w = await WorldAsync(admin, "OKUR");
        await PostAsync(admin, Movements, Move(w.Lot, w.A, 3m));

        // Hareket okuyan kişi bakiyeyi görür; parti adını (lots.read) ve lokasyon adını (locations.read) seçim kutusundan okuyamaz.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Balances}?lotId={w.Lot}")).StatusCode);
        foreach (var (key, id) in new[] { (WarehouseAccess.MovementLotLookup, w.Lot), (WarehouseAccess.MovementToLocationLookup, w.A) })
        {
            var response = await reader.PostAsJsonAsync($"/api/lookups/{key}/resolve", new ResolveLookupRequest([id]));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                Assert.DoesNotContain((await response.Content.ReadFromJsonAsync<LookupResolveDto>())!.Items, i => i.Label is { Length: > 0 });
            }
            else
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    // ---- Yardımcılar ----

    private static JsonObject Move(Guid lot, Guid to, decimal quantity) =>
        new() { ["typeKey"] = "Receipt", ["lotId"] = lot.ToString(), ["toLocationId"] = to.ToString(), ["quantity"] = quantity };

    private sealed record World(Guid StockItem, Guid Owner, Guid Lot, Guid A, Guid B);

    private async Task<World> WorldAsync(HttpClient admin, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var facility = await PostAsync(admin, "/api/records/warehouse.facility", new JsonObject { ["code"] = $"T{suffix}", ["name"] = "Ekran tesisi" });
        await PostAsync(admin, "/api/records/warehouse.location-level", new JsonObject
        {
            ["facilityId"] = facility.Id.ToString(), ["position"] = 1, ["key"] = "bin", ["name"] = "Göz",
            ["characterSet"] = "alphanumeric", ["minLength"] = 1, ["maxLength"] = 8,
        });
        var a = await PostAsync(admin, "/api/records/warehouse.location", new JsonObject { ["facilityId"] = facility.Id.ToString(), ["value"] = "A1" });
        var b = await PostAsync(admin, "/api/records/warehouse.location", new JsonObject { ["facilityId"] = facility.Id.ToString(), ["value"] = "B1" });
        var item = await PostAsync(admin, "/api/records/warehouse.stock-item", new JsonObject { ["code"] = $"{name}-{suffix}", ["name"] = $"{name} ürünü" });
        var owner = await PostAsync(admin, "/api/records/core.party", new JsonObject { ["name"] = $"Malik {suffix}", ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        var lot = await PostAsync(admin, Lots, new JsonObject { ["lotNo"] = $"LOT-{suffix}", ["stockItemId"] = item.Id.ToString(), ["ownerPartyId"] = owner.Id.ToString() });
        return new World(item.Id, owner.Id, lot.Id, a.Id, b.Id);
    }

    private async Task<(ModulixUser Admin, ModulixUser MovementsReader)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [MovementsRead], Scope.All, reader);
        return (admin, reader);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<LookupPageDto> SearchAsync(HttpClient client, string key, string? text)
    {
        var response = await client.GetAsync($"/api/lookups/{key}/search?page=1{(text is null ? string.Empty : $"&q={Uri.EscapeDataString(text)}")}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LookupPageDto>())!;
    }

    private static async Task<IReadOnlyList<LookupItemDto>> ResolveAsync(HttpClient client, string key, IReadOnlyList<Guid> ids)
    {
        var response = await client.PostAsJsonAsync($"/api/lookups/{key}/resolve", new ResolveLookupRequest(ids));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LookupResolveDto>())!.Items;
    }

    private static async Task<List<string>> ValuesAsync(HttpClient client, string url, string field, string? query)
    {
        var response = await client.GetAsync($"{url}?pageSize=100{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items.Select(i => i.Values[field]!.GetValue<string>())];
    }

    private static async Task<List<decimal>> QuantitiesAsync(HttpClient client, string url, string? query)
    {
        var response = await client.GetAsync($"{url}?pageSize=100{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items.Select(i => i.Values["quantity"]!.GetValue<decimal>())];
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
