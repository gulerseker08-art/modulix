using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 3c: stok kartı. Mutlu yol (oluşturma, varsayılan birim, GTİP normalleştirme ve baştaki sıfır, süzme, arama,
/// güncelleme, silme), doğrulama (kod biçimi ve benzersizliği, GTİP, birim, zorunlu alanlar), tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseStockItemTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Items = "/api/records/warehouse.stock-item";

    [Fact]
    public async Task Stock_item_is_created_with_default_unit_and_normalized_gtip_listed_filtered_updated_and_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        // Birim verilmezse listenin varsayılanı atanır; GTİP ayraçları atılır ve baştaki sıfır korunur (metin saklanır).
        var cocoa = await PostAsync(admin, new JsonObject { ["code"] = "KKO-001", ["name"] = "Kakao tozu", ["gtip"] = "1805.00.00.00.00" });
        Assert.Equal("Piece", cocoa.Values["unitKey"]!.GetValue<string>());
        Assert.Equal("180500000000", cocoa.Values["gtip"]!.GetValue<string>());
        var tea = await PostAsync(admin, new JsonObject { ["code"] = "CAY-002", ["name"] = "Siyah çay", ["unitKey"] = "Kilogram", ["gtip"] = "0902 30 00" });
        Assert.Equal("09023000", tea.Values["gtip"]!.GetValue<string>());
        var plain = await PostAsync(admin, new JsonObject { ["code"] = "AMB-003", ["name"] = "Ambalaj", ["isActive"] = false });
        Assert.Null(plain.Values["gtip"]);

        Assert.Equal([tea.Id], (await ListAsync(admin, "gtip=09")).Select(i => i.Id));
        Assert.Equal([cocoa.Id], (await ListAsync(admin, "gtip=1805")).Select(i => i.Id));
        Assert.Equal([tea.Id], (await ListAsync(admin, "unitKey=Kilogram")).Select(i => i.Id));
        Assert.Equal([plain.Id], (await ListAsync(admin, "isActive=false")).Select(i => i.Id));
        Assert.Equal([cocoa.Id], (await ListAsync(admin, "search=kakao")).Select(i => i.Id));
        Assert.Equal([tea.Id], (await ListAsync(admin, "search=0902")).Select(i => i.Id));
        Assert.Equal(["AMB-003", "CAY-002", "KKO-001"], (await ListAsync(admin, null)).Select(i => i.Values["code"]!.GetValue<string>()));

        // GTİP temizlenebilir; güncelleme sürümü artırır.
        var update = await admin.PutAsJsonAsync($"{Items}/{cocoa.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Kakao tozu 25 kg", ["gtip"] = null }, cocoa.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal("Kakao tozu 25 kg", updated.Values["name"]!.GetValue<string>());
        Assert.Null(updated.Values["gtip"]);
        Assert.True(updated.Version > cocoa.Version);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Items}/{updated.Id}?version={updated.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Items}/{updated.Id}")).StatusCode);
    }

    [Fact]
    public async Task Stock_item_code_gtip_unit_and_required_fields_are_validated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await PostAsync(admin, new JsonObject { ["code"] = "Sku-1", ["name"] = "İlk kart" });

        // Kod tenant içinde benzersizdir (büyük/küçük harf yok sayılır); biçim denetlenir.
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "SKU-1", ["name"] = "Çift" })));
        Assert.Equal(["Bu kod başka bir stok kartında kullanılıyor."], duplicate.Errors["code"]);
        var badCode = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "SKU 2", ["name"] = "Boşluklu" })));
        Assert.Contains("code", badCode.Errors.Keys);

        // GTİP yalnızca rakam ve 6–14 hane; hata iletisi anlaşılırdır.
        var letters = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "G1", ["name"] = "Harfli", ["gtip"] = "8471AB" })));
        Assert.Contains("rakam", letters.Errors["gtip"][0]);
        var tooShort = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "G2", ["name"] = "Kısa", ["gtip"] = "8471" })));
        Assert.Contains("hane", tooShort.Errors["gtip"][0]);

        var unknownUnit = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "U1", ["name"] = "Birimsiz", ["unitKey"] = "Uydurma" })));
        Assert.Equal(["Listede olmayan değer."], unknownUnit.Errors["unitKey"]);
        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["description"] = "x" })));
        Assert.Contains("code", missing.Errors.Keys);
        Assert.Contains("name", missing.Errors.Keys);

        // Güncellemede de aynı kurallar: başka kartın koduna geçilemez.
        var other = await PostAsync(admin, new JsonObject { ["code"] = "SKU-9", ["name"] = "Başka kart" });
        var clash = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Items}/{other.Id}", new RecordWriteRequest(new JsonObject { ["code"] = "sku-1" }, other.Version)));
        Assert.Equal(["Bu kod başka bir stok kartında kullanılıyor."], clash.Errors["code"]);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_stock_items_and_may_reuse_the_same_code()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var itemA = await PostAsync(adminA, new JsonObject { ["code"] = "ORTAK-1", ["name"] = "A kartı", ["gtip"] = "090230" });

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Items}/{itemA.Id}")).StatusCode);
        Assert.DoesNotContain(await ListAsync(adminB, null), i => i.Id == itemA.Id);
        Assert.Empty(await ListAsync(adminB, "gtip=0902"));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Items}/{itemA.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Sızıntı" }, itemA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Items}/{itemA.Id}?version={itemA.Version}")).StatusCode);

        // Benzersizlik TenantId ile birlikte tanımlıdır: B aynı kodla kendi kartını açar.
        var itemB = await PostAsync(adminB, new JsonObject { ["code"] = "ORTAK-1", ["name"] = "B kartı" });
        Assert.NotEqual(itemA.Id, itemB.Id);
        Assert.Equal("A kartı", (await adminA.GetFromJsonAsync<RecordResponse>($"{Items}/{itemA.Id}"))!.Values["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stock_items_need_their_own_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var facilityOnly = await LoginAsync(s.FacilityOnly);
        var item = await PostAsync(admin, new JsonObject { ["code"] = "YET-1", ["name"] = "Yetki kartı" });

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Items}/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "R-1", ["name"] = "Okuyucu" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Items}/{item.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "x" }, item.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Items}/{item.Id}?version={item.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Items, new RecordWriteRequest(new JsonObject { ["code"] = "W-1", ["name"] = "Yazıcı" }))).StatusCode);

        // Tesis izni stok kartı izni vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync(Items)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await facilityOnly.GetAsync("/api/records/warehouse.facility")).StatusCode);
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser FacilityOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var facilityOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [StockItemsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [StockItemsRead, StockItemsWrite], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead], Scope.All, facilityOnly);
        return (admin, reader, writer, facilityOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query)
    {
        var response = await client.GetAsync($"{Items}?pageSize=50{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(Items, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
