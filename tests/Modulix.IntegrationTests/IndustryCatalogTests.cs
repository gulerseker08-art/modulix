using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0043: firmanın sektörü platform kataloğundan (<c>platform.Industries</c>) seçilir ve ortak firma kaydında (<c>core.party</c>)
/// kodla tutulur. Katalog platform geneli üründür; değerler tenant verisidir.
/// </summary>
public class IndustryCatalogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Companies = "/api/crm/companies";
    private const string Parties = "/api/records/" + SharedEntities.Party;

    [Fact]
    public async Task The_catalog_lists_the_seed_industries_and_a_company_keeps_the_chosen_code_with_its_name()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);

        var industries = (await client.GetFromJsonAsync<List<IndustryDto>>("/api/industries"))!;
        Assert.Contains(industries, i => i is { Code: "gida-icecek", Name: "Gıda ve içecek", IsActive: true });
        Assert.Contains(industries, i => i.Code == "lojistik-tasimacilik");

        // Kod verilir, ad katalogdan gelir; ad ile de seçilebilir (içe aktarma, eski metin) ve kayda kod yazılır.
        var created = await PostCompanyAsync(client, new SaveCompanyRequest("Sektörlü Gıda A.Ş.", Industry: "gida-icecek"));
        Assert.Equal(("gida-icecek", "Gıda ve içecek"), (created.Industry, created.IndustryName));

        var renamed = await PutCompanyAsync(client, created, "Lojistik ve taşımacılık");
        Assert.Equal(("lojistik-tasimacilik", "Lojistik ve taşımacılık"), (renamed.Industry, renamed.IndustryName));
        var loaded = (await client.GetFromJsonAsync<CompanyDto>($"{Companies}/{created.Id}"))!;
        Assert.Equal(("lojistik-tasimacilik", "Lojistik ve taşımacılık"), (loaded.Industry, loaded.IndustryName));

        // Sektör temizlenebilir.
        var cleared = await PutCompanyAsync(client, loaded, null);
        Assert.Null(cleared.Industry);
        Assert.Null(cleared.IndustryName);
    }

    [Fact]
    public async Task The_company_list_filters_and_sorts_by_industry()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        await PostCompanyAsync(client, new SaveCompanyRequest("Sıra Gıda", Industry: "gida-icecek"));
        await PostCompanyAsync(client, new SaveCompanyRequest("Sıra Tekstil", Industry: "tekstil-hazir-giyim"));
        await PostCompanyAsync(client, new SaveCompanyRequest("Sıra Sektörsüz"));

        Assert.Equal(["Sıra Gıda"], await NamesAsync(client, "industry=gida-icecek"));
        // Ada göre artan sıra: "Gıda ve içecek" < "Tekstil ve hazır giyim"; sektörsüz en sonda.
        Assert.Equal(["Sıra Gıda", "Sıra Tekstil", "Sıra Sektörsüz"], await NamesAsync(client, "sort=industry"));
        Assert.Equal(["Sıra Tekstil", "Sıra Gıda", "Sıra Sektörsüz"], await NamesAsync(client, "sort=-industry"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Companies}?industry=B%C3%BCy%C3%BCk")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_industry_is_rejected_and_nothing_is_written()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);

        foreach (var value in new[] { "yok-boyle-sektor", "Serbest yazılmış sektör" })
        {
            var response = await client.PostAsJsonAsync(Companies, new SaveCompanyRequest("Reddedilen A.Ş.", Industry: value));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("industry", (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        }

        Assert.Empty(await NamesAsync(client, "search=Reddedilen"));

        // Ortak firma kaydı (core.party) aynı kuralı uygular: kod katalogda olmalı.
        var party = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject { ["name"] = "Reddedilen Taraf", ["industryCode"] = "yok-boyle-sektor" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, party.StatusCode);
        Assert.Contains("industryCode", (await party.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
    }

    [Fact]
    public async Task Other_modules_read_the_same_value_from_the_shared_party_record()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        var company = await PostCompanyAsync(client, new SaveCompanyRequest("Ortak Sektör A.Ş.", Industry: "enerji"));

        // CRM firması ve ortak kaydı aynı sektörü gösterir (kimlik aynı: firma → taraf).
        var party = (await client.GetFromJsonAsync<RecordResponse>($"{Parties}/{company.PartyId}"))!;
        Assert.Equal("enerji", party.Values["industryCode"]!.GetValue<string>());

        // Taraf üzerinden değişen sektör CRM firmasında da görünür.
        var changed = await client.PutAsJsonAsync($"{Parties}/{party.Id}", new RecordWriteRequest(new JsonObject { ["industryCode"] = "madencilik" }, party.Version));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("madencilik", (await client.GetFromJsonAsync<CompanyDto>($"{Companies}/{company.Id}"))!.Industry);
    }

    [Fact]
    public async Task A_deactivated_industry_stays_on_existing_companies_but_cannot_be_newly_chosen()
    {
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var code = $"sek-{Guid.NewGuid():N}"[..20];
        var added = await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest(code, "Geçici sektör " + code));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var (_, admin) = await CreateTenantAdminAsync();
        using var client = await LoginAsync(admin);
        var company = await PostCompanyAsync(client, new SaveCompanyRequest("Pasif Sektör A.Ş.", Industry: code));

        var off = await platform.PutAsJsonAsync($"/api/platform/sectors/industries/{code}/active", new SetActiveRequest(false));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<IndustryDto>())!.IsActive);

        // Mevcut kayıt sektörünü korur ve başka alanı düzenlenebilir; yeni kayıt pasif sektörü seçemez.
        var edited = await client.PutAsJsonAsync($"{Companies}/{company.Id}", Save(company, c => c with { City = "Bursa" }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(code, (await edited.Content.ReadFromJsonAsync<CompanyDto>())!.Industry);
        var another = await client.PostAsJsonAsync(Companies, new SaveCompanyRequest("Yeni Pasif A.Ş.", Industry: code));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, another.StatusCode);
        Assert.Contains("industry", (await another.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);

        // Liste pasifi de verir (kayıtlardaki eski değer adıyla görünsün).
        Assert.Contains((await client.GetFromJsonAsync<List<IndustryDto>>("/api/industries"))!, i => i.Code == code && !i.IsActive);
    }

    [Fact]
    public async Task Catalog_management_validates_codes_and_names_and_is_platform_only()
    {
        var (_, admin) = await CreateTenantAdminAsync();
        using var adminClient = await LoginAsync(admin);
        using var anonymous = factory.CreateBrowserClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/industries")).StatusCode);
        foreach (var client in new[] { adminClient, anonymous })
        {
            var status = (await client.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest("deneme-sektor", "Deneme"))).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"sektör ekleme: {status}");
            status = (await client.PutAsJsonAsync("/api/platform/sectors/industries/enerji/active", new SetActiveRequest(false))).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"sektör pasife alma: {status}");
            status = (await client.GetAsync("/api/platform/sectors/industries")).StatusCode;
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"platform listesi: {status}");
        }

        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var code = $"sek-{Guid.NewGuid():N}"[..20];
        Assert.Equal(HttpStatusCode.Created, (await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest(code, "Ad " + code))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest(code, "Başka ad " + code))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest(code + "-2", "Ad " + code))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest("Kötü Kod", "Ad"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/sectors/industries", new CreateIndustryRequest(code + "-3", " "))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync("/api/platform/sectors/industries/yok/active", new SetActiveRequest(true))).StatusCode);
        Assert.Contains((await platform.GetFromJsonAsync<List<IndustryDto>>("/api/platform/sectors/industries"))!, i => i.Code == code);
    }

    [Fact]
    public async Task A_tenant_never_sees_or_filters_another_tenants_company_sector()
    {
        var (_, adminA) = await CreateTenantAdminAsync();
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var companyA = await PostCompanyAsync(clientA, new SaveCompanyRequest("İzole Sektör A", Industry: "otomotiv"));

        Assert.Equal(["İzole Sektör A"], await NamesAsync(clientA, "industry=otomotiv&search=İzole"));
        Assert.Empty(await NamesAsync(clientB, "industry=otomotiv&search=İzole"));
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Companies}/{companyA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Parties}/{companyA.PartyId}")).StatusCode);

        // Katalog ortaktır: B de aynı listeyi görür, ama A'nın firmasının sektörünü değiştiremez.
        Assert.Contains((await clientB.GetFromJsonAsync<List<IndustryDto>>("/api/industries"))!, i => i.Code == "otomotiv");
        var put = await clientB.PutAsJsonAsync($"{Companies}/{companyA.Id}", Save(companyA, c => c with { Industry = "enerji" }));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("otomotiv", (await clientA.GetFromJsonAsync<CompanyDto>($"{Companies}/{companyA.Id}"))!.Industry);
    }

    [Fact]
    public async Task A_read_only_user_sees_the_sector_but_cannot_change_it()
    {
        var (tenant, admin) = await CreateTenantAdminAsync();
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, reader);
        using var adminClient = await LoginAsync(admin);
        using var readerClient = await LoginAsync(reader);
        var company = await PostCompanyAsync(adminClient, new SaveCompanyRequest("Yetki Sektör A.Ş.", Industry: "turizm-konaklama"));

        var seen = (await readerClient.GetFromJsonAsync<CompanyDto>($"{Companies}/{company.Id}"))!;
        Assert.Equal(("turizm-konaklama", "Turizm ve konaklama"), (seen.Industry, seen.IndustryName));
        var denied = await readerClient.PutAsJsonAsync($"{Companies}/{company.Id}", Save(seen, c => c with { Industry = "enerji" }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("turizm-konaklama", (await adminClient.GetFromJsonAsync<CompanyDto>($"{Companies}/{company.Id}"))!.Industry);
    }

    private static SaveCompanyRequest Save(CompanyDto c, Func<SaveCompanyRequest, SaveCompanyRequest> change) =>
        change(new SaveCompanyRequest(
            c.Name, c.TaxNumber, c.Phone, c.Email, c.Website, c.City, c.Notes, c.Version, c.Address, c.Industry, c.Status, c.Source, c.Tags, c.OwnerId, PartyId: null, Kind: c.Kind, TaxOffice: c.TaxOffice));

    private async Task<CompanyDto> PostCompanyAsync(HttpClient client, SaveCompanyRequest request)
    {
        var response = await client.PostAsJsonAsync(Companies, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private async Task<CompanyDto> PutCompanyAsync(HttpClient client, CompanyDto company, string? industry)
    {
        var response = await client.PutAsJsonAsync($"{Companies}/{company.Id}", Save(company, c => c with { Industry = industry }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<List<string>> NamesAsync(HttpClient client, string query)
    {
        var page = await client.GetFromJsonAsync<CrmPage<CompanyDto>>($"{Companies}?pageSize=100&{query}");
        return [.. page!.Items.Select(c => c.Name)];
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> CreateTenantAdminAsync()
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
