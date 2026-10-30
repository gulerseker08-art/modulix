using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0018: ürün/hizmet kataloğu: kod benzersizliği, tenant izolasyonu, yaşam döngüsü, birim koruması, maliyet yetkisi.</summary>
public class ProductCatalogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CatalogAccess = [ProductsRead, ProductsWrite];
    private static readonly string[] CostAccess = [ProductsRead, ProductsWrite, ProductCostsRead];

    [Fact]
    public async Task Two_tenants_can_use_the_same_code_but_a_tenant_cannot_repeat_it_or_reach_the_others_records()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var unitA = await CreateUnitAsync(adminA, "ADT");
        var unitB = await CreateUnitAsync(adminB, "ADT");
        var productA = await PostAsync<ProductDto>(adminA, "/api/crm/products", NewProduct("P-001", unitA.Id));
        await PostAsync<ProductDto>(adminB, "/api/crm/products", NewProduct("P-001", unitB.Id));

        // Aynı tenant'ta mükerrer kod 422.
        var duplicate = await adminA.PostAsJsonAsync("/api/crm/products", NewProduct("p-001", unitA.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("code", await duplicate.Content.ReadAsStringAsync());

        // Başka tenant'ın kaydı 404; başka tenant'ın birimiyle ürün açılamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/products/{productA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await adminB.PostAsJsonAsync("/api/crm/products", NewProduct("P-002", unitA.Id))).StatusCode);
        Assert.Single((await adminB.GetFromJsonAsync<CrmPage<ProductDto>>("/api/crm/products"))!.Items);
    }

    [Fact]
    public async Task Lifecycle_dates_and_deletion_rules_are_enforced_on_the_server()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var unit = await CreateUnitAsync(admin, "ADT");

        // Satış bitişi başlangıçtan önce olamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/products",
            NewProduct("D-1", unit.Id) with { SaleStart = new DateOnly(2026, 12, 1), SaleEnd = new DateOnly(2026, 11, 1) })).StatusCode);
        // Dönemsel ücret dönem ister; ürün kartında hizmet süresi girilemez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/products",
            NewProduct("D-2", unit.Id) with { ChargeType = "Recurring" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/products",
            NewProduct("D-3", unit.Id) with { ServiceDurationMinutes = 30 })).StatusCode);

        var service = await PostAsync<ProductDto>(admin, "/api/crm/products",
            NewProduct("S-1", unit.Id) with { Kind = "Service", ServiceDurationMinutes = 90, ChargeType = "Recurring", RecurringPeriod = "Monthly" });
        Assert.Equal("Draft", service.Status);

        var active = await PutAsync(admin, service, Save(service) with { Status = "Active" });
        Assert.Equal("Active", active.Status);

        // Aktif ürün silinmez, pasifleştirilir; satıştan kaldırılan geri açılamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/products/{active.Id}?version={active.Version}")).StatusCode);
        var withdrawn = await PutAsync(admin, active, Save(active) with { Status = "Withdrawn" });
        var back = await admin.PutAsJsonAsync($"/api/crm/products/{withdrawn.Id}", Save(withdrawn) with { Status = "Active" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, back.StatusCode);

        // Taslak silinebilir.
        var draft = await PostAsync<ProductDto>(admin, "/api/crm/products", NewProduct("D-9", unit.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/crm/products/{draft.Id}?version={draft.Version}")).StatusCode);

        // Eski sürümle yazım kayıp güncelleme üretmez.
        var stale = await admin.PutAsJsonAsync($"/api/crm/products/{active.Id}", Save(active) with { Name = "Eski sürüm" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Used_unit_factor_cannot_change_and_invalid_factor_or_category_cycle_is_rejected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var kg = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("KG", "Kilogram", "weight", 1000m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/units", new SaveMeasureUnitRequest("BAD", "Hatalı", "weight", 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/units", new SaveMeasureUnitRequest("KG", "Kopya", "weight", 1m))).StatusCode);

        // Kullanılmadan önce değişebilir; ürün kullanınca katsayı ve grup kilitlenir.
        var edited = await admin.PutAsJsonAsync($"/api/crm/units/{kg.Id}", new SaveMeasureUnitRequest("KG", "Kilogram", "weight", 1001m, kg.Version));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var current = (await edited.Content.ReadFromJsonAsync<MeasureUnitDto>())!;
        await PostAsync<ProductDto>(admin, "/api/crm/products", NewProduct("U-1", kg.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/crm/units/{kg.Id}", new SaveMeasureUnitRequest("KG", "Kilogram", "weight", 1m, current.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/units/{kg.Id}?version={current.Version}")).StatusCode);

        // Kategori kendi altına taşınamaz.
        var root = await PostAsync<ProductCategoryDto>(admin, "/api/crm/product-categories", new SaveProductCategoryRequest("Kök"));
        var child = await PostAsync<ProductCategoryDto>(admin, "/api/crm/product-categories", new SaveProductCategoryRequest("Alt", root.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/crm/product-categories/{root.Id}", new SaveProductCategoryRequest("Kök", child.Id, true, root.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/product-categories/{root.Id}?version={root.Version}")).StatusCode);
    }

    [Fact]
    public async Task Cost_is_hidden_and_unwritable_without_the_cost_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var viewer = await LoginAsync(s.Viewer);
        using var costUser = await LoginAsync(s.CostUser);
        var unit = await CreateUnitAsync(admin, "ADT");

        var created = await PostAsync<ProductDto>(admin, "/api/crm/products", NewProduct("C-1", unit.Id) with { Cost = 42.5m });
        Assert.Equal(42.5m, created.Cost);

        var seen = (await viewer.GetFromJsonAsync<ProductDto>($"/api/crm/products/{created.Id}"))!;
        Assert.Null(seen.Cost);
        // Ham yanıtta maliyet değeri geçmez ("42" aranmaz: kimliklerdeki rastgele GUID'ler "42" içerebilir, test kararsızlaşır).
        Assert.DoesNotContain("42.5", await viewer.GetStringAsync("/api/crm/products"));

        // Maliyet izni olmayan, maliyet göndererek kayıt açıp güncelleyemez; mevcut maliyet ezilmez.
        var update = await viewer.PutAsJsonAsync($"/api/crm/products/{created.Id}", Save(created) with { Name = "Yeni ad", Cost = 1m });
        Assert.True(update.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity, update.StatusCode.ToString());
        Assert.Equal(42.5m, (await costUser.GetFromJsonAsync<ProductDto>($"/api/crm/products/{created.Id}"))!.Cost);
    }

    private static SaveProductRequest NewProduct(string code, Guid unitId) => new(code, $"Ürün {code}", unitId);

    private static SaveProductRequest Save(ProductDto p) => new(
        p.Code, p.Name, p.BaseUnitId, p.Kind, p.Status, p.Description, p.CategoryId, p.ExternalCode, p.Barcode, p.Brand, p.Model, p.Tags,
        p.SaleStart, p.SaleEnd, p.TaxCategory, p.PriceIncludesTax, p.QuantityPrecision, p.Cost, p.CostCurrency ?? "TRY",
        p.ServiceDurationMinutes, p.DeliveryMode, p.ChargeType, p.RecurringPeriod, p.Version);

    private static Task<MeasureUnitDto> CreateUnitAsync(HttpClient client, string code) =>
        PostAsync<MeasureUnitDto>(client, "/api/crm/units", new SaveMeasureUnitRequest(code, $"Birim {code}", "count"));

    private static async Task<ProductDto> PutAsync(HttpClient client, ProductDto current, SaveProductRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/crm/products/{current.Id}", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Viewer, ModulixUser CostUser)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var costUser = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, CatalogAccess, Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, CostAccess, Scope.All, costUser);
        return (admin, viewer, costUser);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
