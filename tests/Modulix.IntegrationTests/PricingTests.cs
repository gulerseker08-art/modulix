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

/// <summary>ADR-0018: fiyat listeleri, kademe, birim dönüşümü, indirim, vergi, kur, paket döngüsü, yetki ve tenant izolasyonu.</summary>
public class PricingTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CatalogOnly = [ProductsRead];

    [Fact]
    public async Task Calculation_converts_units_picks_the_tier_applies_discount_and_tax_and_rounds()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var kutu = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("KUTU", "Kutu", "count", 12m));
        await PostAsync<TaxRateDto>(admin, "/api/crm/tax-rates", new SaveTaxRateRequest("KDV20", "KDV %20", 20m, new DateOnly(2020, 1, 1)));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active", TaxCategory = "KDV20" });

        var list = await PostAsync<PriceListDto>(admin, "/api/crm/price-lists", new SavePriceListRequest("Genel liste"));
        await AddTierAsync(admin, list.Id, product.Id, 1m, 100m, max: 10m);
        await AddTierAsync(admin, list.Id, product.Id, 10m, 90m);
        await PublishAsync(admin, list);
        var discount = await PostAsync<DiscountPolicyDto>(admin, "/api/crm/discount-policies", new SaveDiscountPolicyRequest("Yüzde 10", Percent: 10m, Priority: 1));

        // 2 kutu = 24 adet → 10 ve üzeri kademe (90) → 2160; %10 → 1944; KDV %20 → 388,80; brüt 2332,80.
        var line = await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 2m, "TRY", kutu.Id, DiscountPolicyIds: [discount.Id]));
        Assert.Equal(24m, line.BaseQuantity);
        Assert.Equal(2160m, line.ListTotal);
        Assert.Equal(1944m, line.DiscountedTotal);
        Assert.Equal(10m, line.EffectiveDiscountPercent);
        Assert.Equal(20m, line.TaxRatePercent);
        Assert.Equal((1944m, 388.80m, 2332.80m), (line.Net, line.Tax, line.Gross));
        Assert.Equal(list.Id, line.PriceListId);
        Assert.False(line.RequiresApproval);

        // Küçük miktar ilk kademede: 3 adet × 100.
        var small = await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 3m, "TRY"));
        Assert.Equal(300m, small.ListTotal);

        // Ondalık miktar, hassasiyeti 0 olan ürün için reddedilir.
        var fractional = await admin.PostAsJsonAsync("/api/crm/pricing/calculate", new PriceCalculationRequestDto(product.Id, 1.5m, "TRY"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fractional.StatusCode);
        Assert.Equal("invalid-quantity", (await fractional.Content.ReadFromJsonAsync<PriceCalculationErrorDto>())!.Code);
    }

    [Fact]
    public async Task Discount_above_the_policy_threshold_is_flagged_for_approval_and_manual_discount_cannot_exceed_the_cap()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active" });
        var list = await PostAsync<PriceListDto>(admin, "/api/crm/price-lists", new SavePriceListRequest("Genel"));
        await AddTierAsync(admin, list.Id, product.Id, 1m, 100m);
        await PublishAsync(admin, list);
        var policy = await PostAsync<DiscountPolicyDto>(admin, "/api/crm/discount-policies",
            new SaveDiscountPolicyRequest("Kampanya", Percent: 15m, Priority: 1, MaxTotalPercent: 20m, ApprovalAbovePercent: 10m));

        // %15 politika + %10 elle = ardışık %23,5 → azami %20 sınırına çekilir ve onay gerekir.
        var line = await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY", DiscountPolicyIds: [policy.Id], ManualDiscountPercent: 10m));
        Assert.True(line.DiscountCapApplied);
        Assert.Equal(20m, line.EffectiveDiscountPercent);
        Assert.Equal(80m, line.DiscountedTotal);
        Assert.True(line.RequiresApproval);

        // Tutar ve yüzde birlikte verilemez; ikisi de boş olamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/discount-policies", new SaveDiscountPolicyRequest("Hatalı", Percent: 5m, Amount: 5m, Currency: "TRY"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/discount-policies", new SaveDiscountPolicyRequest("Boş"))).StatusCode);
    }

    [Fact]
    public async Task Published_price_lists_are_immutable_and_invalid_tiers_or_empty_publish_are_rejected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active" });
        var list = await PostAsync<PriceListDto>(admin, "/api/crm/price-lists", new SavePriceListRequest("Liste"));

        // Satırsız liste yayımlanamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutListAsync(admin, list, "Published")).StatusCode);

        var tier = await AddTierAsync(admin, list.Id, product.Id, 1m, 100m, max: 10m);
        // Çakışan kademe, ters aralık ve negatif fiyat reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 5m, 90m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 20m, 90m, MaxQuantity: 20m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 20m, -1m))).StatusCode);

        var published = await PublishAsync(admin, list);
        Assert.NotNull(published.PublishedAt);

        // Yayımlanan listeye satır eklenemez, silinemez; liste yeniden adlandırılamaz, silinemez, taslağa dönemez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 10m, 90m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/price-list-items/{tier.Id}?version={tier.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/crm/price-lists/{list.Id}", Save(published) with { Name = "Yeni ad" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/price-lists/{list.Id}?version={published.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutListAsync(admin, published, "Draft")).StatusCode);

        // Arşive alınabilir; arşivlenen liste artık fiyat vermez.
        var archived = await PutListAsync(admin, published, "Archived");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var missing = await admin.PostAsJsonAsync("/api/crm/pricing/calculate", new PriceCalculationRequestDto(product.Id, 1m, "TRY"));
        Assert.Equal("price-not-found", (await missing.Content.ReadFromJsonAsync<PriceCalculationErrorDto>())!.Code);
    }

    [Fact]
    public async Task Price_selection_prefers_the_customer_list_and_reports_missing_conflicting_and_expired_prices_explicitly()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active" });
        var company = await PostAsync<CompanyDto>(admin, "/api/crm/companies", new SaveCompanyRequest("Müşteri A.Ş.", null, null, null, null, null, null));

        // Hiç yayımlanmış liste yok → fiyat yok hatası, sıfır fiyat değil.
        Assert.Equal("price-not-found", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY")));

        var general = await NewPublishedListAsync(admin, product.Id, "Genel", 100m);
        Assert.Equal(100m, (await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY"))).ListTotal);

        // Aynı öncelikte ikinci varsayılan liste → belirsiz, çakışma hatası.
        await NewPublishedListAsync(admin, product.Id, "Genel 2", 95m);
        Assert.Equal("price-conflict", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY")));

        // Açık seçim çakışmayı çözer; başka para birimi fiyat bulamaz.
        Assert.Equal(100m, (await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY", PriceListId: general.Id))).ListTotal);
        Assert.Equal("price-not-found", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "USD")));

        // Müşteriye özel liste, varsayılanların yerine geçer; yalnızca o firma için.
        await NewPublishedListAsync(admin, product.Id, "Müşteri listesi", 80m, scope: "Customer", companyId: company.Id);
        Assert.Equal(80m, (await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY", CompanyId: company.Id))).ListTotal);
        Assert.Equal("price-conflict", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY")));

        // Süresi dolmuş liste kullanılmaz (bitiş hariç).
        var now = DateTimeOffset.UtcNow;
        var expiredProduct = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-2", adt.Id) with { Status = "Active" });
        await NewPublishedListAsync(admin, expiredProduct.Id, "Eski", 10m, validFrom: now.AddDays(-10), validTo: now.AddDays(-1));
        Assert.Equal("price-not-found", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(expiredProduct.Id, 1m, "TRY")));

        // Ücretsiz satış, eksik fiyattan ayrıdır.
        var freeProduct = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-3", adt.Id) with { Status = "Active" });
        var freeList = await PostAsync<PriceListDto>(admin, "/api/crm/price-lists", new SavePriceListRequest("Ücretsiz", Segment: null, Scope: "Segment") with { Scope = "Segment", Segment = "hediye" });
        await AddTierAsync(admin, freeList.Id, freeProduct.Id, 1m, 0m, free: true);
        await PublishAsync(admin, freeList);
        var free = await CalculateAsync(admin, new PriceCalculationRequestDto(freeProduct.Id, 1m, "TRY", Segment: "hediye"));
        Assert.True(free.IsFree);
        Assert.Equal(0m, free.Gross);
    }

    [Fact]
    public async Task Tax_rate_ranges_cannot_overlap_and_a_missing_tax_or_exchange_rate_stops_the_calculation()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active", TaxCategory = "KDV10" });
        await NewPublishedListAsync(admin, product.Id, "Genel", 100m);

        // Kategori verilmiş ama oran yok → hata (sessizce %0 uygulanmaz).
        Assert.Equal("tax-rate-not-found", await ErrorCodeAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY")));

        // Oran tarih aralıklarıyla tutulur; çakışma reddedilir, yeni oran eskisini bitirdikten sonra başlar.
        await PostAsync<TaxRateDto>(admin, "/api/crm/tax-rates", new SaveTaxRateRequest("KDV10", "KDV %10", 10m, new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/tax-rates", new SaveTaxRateRequest("KDV10", "Çakışan", 12m, new DateOnly(2025, 1, 1)))).StatusCode);
        await PostAsync<TaxRateDto>(admin, "/api/crm/tax-rates", new SaveTaxRateRequest("KDV10", "KDV %12", 12m, new DateOnly(2030, 1, 1)));
        Assert.Equal(110m, (await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY", At: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)))).Gross);
        Assert.Equal(112m, (await CalculateAsync(admin, new PriceCalculationRequestDto(product.Id, 1m, "TRY", At: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)))).Gross);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/tax-rates", new SaveTaxRateRequest("YANLIS", "Hatalı", 101m, new DateOnly(2020, 1, 1)))).StatusCode);

        // Gösterim para birimi için kur şarttır; ters kur da kullanılır; aynı gün için ikinci kayıt reddedilir.
        var request = new PriceCalculationRequestDto(product.Id, 1m, "TRY", DisplayCurrency: "USD", At: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("exchange-rate-not-found", await ErrorCodeAsync(admin, request));
        await PostAsync<ExchangeRateDto>(admin, "/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 40m, new DateOnly(2026, 5, 30), Note: "Elle giriş"));
        var display = (await CalculateAsync(admin, request)).Display!;
        Assert.Equal("USD", display.Currency);
        Assert.Equal(2.75m, display.Gross);
        Assert.Equal(new DateOnly(2026, 5, 30), display.RateDate);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 41m, new DateOnly(2026, 5, 30)))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/exchange-rates", new SaveExchangeRateRequest("TRY", "TRY", 1m, new DateOnly(2026, 5, 30)))).StatusCode);
    }

    [Fact]
    public async Task Package_components_reject_self_reference_duplicates_and_cycles()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var a = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("PKT-A", adt.Id));
        var b = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("PKT-B", adt.Id));
        var c = await PostAsync<ProductDto>(admin, "/api/crm/products", Product("PKT-C", adt.Id));

        await PostAsync<ProductComponentDto>(admin, "/api/crm/product-components", new SaveProductComponentRequest(a.Id, b.Id, 2m));
        await PostAsync<ProductComponentDto>(admin, "/api/crm/product-components", new SaveProductComponentRequest(b.Id, c.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/product-components", new SaveProductComponentRequest(a.Id, a.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/product-components", new SaveProductComponentRequest(a.Id, b.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/product-components", new SaveProductComponentRequest(b.Id, a.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync("/api/crm/product-components", new SaveProductComponentRequest(c.Id, a.Id))).StatusCode);

        // Bileşenler paket hesabında listelenir (miktar × bileşen miktarı).
        var unitPrice = await NewPublishedListAsync(admin, a.Id, "Paket", 500m);
        var activeA = (await admin.GetFromJsonAsync<ProductDto>($"/api/crm/products/{a.Id}"))!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/crm/products/{a.Id}", Save(activeA) with { Status = "Active" })).StatusCode);
        var line = await CalculateAsync(admin, new PriceCalculationRequestDto(a.Id, 3m, "TRY"));
        Assert.Equal(unitPrice.Id, line.PriceListId);
        var component = Assert.Single(line.Components);
        Assert.Equal("PKT-B", component.Code);
        Assert.Equal(6m, component.BaseQuantity);
    }

    [Fact]
    public async Task Pricing_requires_the_prices_permission_and_never_crosses_tenants()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var catalogOnly = await LoginAsync(a.CatalogOnly);

        var adt = await PostAsync<MeasureUnitDto>(adminA, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(adminA, "/api/crm/products", Product("P-1", adt.Id) with { Status = "Active" });
        var list = await NewPublishedListAsync(adminA, product.Id, "Genel", 100m);

        // Katalog okuyabilen ama fiyat izni olmayan kullanıcı liste göremez, fiyat hesaplayamaz, yazamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await catalogOnly.GetAsync("/api/crm/price-lists")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await catalogOnly.GetAsync("/api/crm/tax-rates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await catalogOnly.PostAsJsonAsync("/api/crm/pricing/calculate", new PriceCalculationRequestDto(product.Id, 1m, "TRY"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await catalogOnly.PostAsJsonAsync("/api/crm/price-lists", new SavePriceListRequest("İzinsiz"))).StatusCode);

        // Başka tenant ne listeyi ne ürünü görür; hesap 404 (ürün yok) döner.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/price-lists/{list.Id}")).StatusCode);
        var crossTenant = await adminB.PostAsJsonAsync("/api/crm/pricing/calculate", new PriceCalculationRequestDto(product.Id, 1m, "TRY"));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<CrmPage<PriceListDto>>("/api/crm/price-lists"))!.Items);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await adminB.PostAsJsonAsync("/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 1m, 1m))).StatusCode);
    }

    private static SaveProductRequest Product(string code, Guid unitId) => new(code, $"Ürün {code}", unitId);

    private static SaveProductRequest Save(ProductDto p) => new(
        p.Code, p.Name, p.BaseUnitId, p.Kind, p.Status, p.Description, p.CategoryId, p.ExternalCode, p.Barcode, p.Brand, p.Model, p.Tags,
        p.SaleStart, p.SaleEnd, p.TaxCategory, p.PriceIncludesTax, p.QuantityPrecision, p.Cost, p.CostCurrency ?? "TRY",
        p.ServiceDurationMinutes, p.DeliveryMode, p.ChargeType, p.RecurringPeriod, p.Version);

    private static SavePriceListRequest Save(PriceListDto l) => new(
        l.Name, l.Currency, l.Scope, l.CompanyId, l.Segment, l.ValidFrom, l.ValidTo, l.Status, l.TierMethod, l.Version);

    private static Task<HttpResponseMessage> PutListAsync(HttpClient client, PriceListDto list, string status) =>
        client.PutAsJsonAsync($"/api/crm/price-lists/{list.Id}", Save(list) with { Status = status });

    private static async Task<PriceListDto> PublishAsync(HttpClient client, PriceListDto list)
    {
        var response = await PutListAsync(client, list, "Published");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PriceListDto>())!;
    }

    private static Task<PriceListItemDto> AddTierAsync(
        HttpClient client, Guid listId, Guid productId, decimal min, decimal price, decimal? max = null, bool free = false) =>
        PostAsync<PriceListItemDto>(client, "/api/crm/price-list-items", new SavePriceListItemRequest(listId, productId, min, price, max, free));

    private static async Task<PriceListDto> NewPublishedListAsync(
        HttpClient client, Guid productId, string name, decimal price, string scope = "Default", Guid? companyId = null,
        DateTimeOffset? validFrom = null, DateTimeOffset? validTo = null)
    {
        var list = await PostAsync<PriceListDto>(client, "/api/crm/price-lists",
            new SavePriceListRequest(name, Scope: scope, CompanyId: companyId, ValidFrom: validFrom, ValidTo: validTo));
        await AddTierAsync(client, list.Id, productId, 1m, price);
        return await PublishAsync(client, list);
    }

    private static async Task<PriceCalculationDto> CalculateAsync(HttpClient client, PriceCalculationRequestDto request)
    {
        var response = await client.PostAsJsonAsync("/api/crm/pricing/calculate", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PriceCalculationDto>())!;
    }

    private static async Task<string> ErrorCodeAsync(HttpClient client, PriceCalculationRequestDto request)
    {
        var response = await client.PostAsJsonAsync("/api/crm/pricing/calculate", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PriceCalculationErrorDto>())!.Code;
    }

    private async Task<(ModulixUser Admin, ModulixUser CatalogOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var catalogOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, CatalogOnly, Scope.All, catalogOnly);
        return (admin, catalogOnly);
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
