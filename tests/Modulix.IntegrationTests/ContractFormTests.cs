using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Customization;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Sözleşme formu metadata'dan gelir (form düzeni ADR-0021): varsayılan düzen her üyeye açıktır, firma alanı zorunlu kılabilir ve
/// zorunluluk sunucuda denetlenir, başka firmanın düzeni etkilenmez, yetkisiz kullanıcı düzeni değiştiremez; sözleşmede özel alan çalışır.
/// </summary>
public class ContractFormTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string EntityKey = "contracts.contract";
    private const string Parties = "/api/records/" + SharedEntities.Party;

    [Fact]
    public async Task Default_layout_is_served_and_a_tenant_required_field_is_enforced_by_the_server_only_for_that_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var layout = await LayoutAsync(adminA);
        Assert.Equal(["genel"], layout.Tabs.Select(t => t.Key));
        Assert.Contains(layout.Fields, f => f is { Key: "title", Fixed: true, Required: true });
        Assert.Contains(layout.Fields, f => f is { Key: "paymentTerms", Required: false, Fixed: false });

        // Firma A ödeme koşullarını zorunlu kılar: boş oluşturma 422, dolu oluşturma 201 (API'den, arayüze bakılmaksızın).
        var request = new SaveFormLayoutRequest(layout.Tabs, layout.Sections,
            [.. layout.Fields.Select(f => new SaveFormFieldRequest(f.Key, f.Section, f.Span, f.Key == "paymentTerms" || f.Required, f.Visible))], layout.Version);
        var saved = await adminA.PutAsJsonAsync($"/api/customization/form-layouts/{EntityKey}", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var missing = await adminA.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Zorunlu alan", a.PartyId));
        Assert.True((await ValidationProblemAsync(missing)).Errors.ContainsKey("paymentTerms"));
        var filled = await adminA.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Zorunlu alan", a.PartyId, PaymentTerms: "30 gün vadeli"));
        Assert.Equal(HttpStatusCode.Created, filled.StatusCode);

        // Firma B etkilenmez.
        Assert.Contains((await LayoutAsync(adminB)).Fields, f => f is { Key: "paymentTerms", Required: false });
        var other = await adminB.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Serbest", b.PartyId));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Custom_fields_work_on_contracts_and_the_layout_places_them()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var entities = (await admin.GetFromJsonAsync<List<CustomizableEntityDto>>("/api/customization/entities"))!;
        Assert.Contains(entities, e => e.Key == EntityKey);

        var created = await admin.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(EntityKey, "SLA seviyesi", CustomFieldType.Text));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var field = (await created.Content.ReadFromJsonAsync<CustomFieldDto>())!;

        var layout = await LayoutAsync(admin);
        Assert.Contains(layout.Fields, f => f.Key == "custom." + field.Key && f.IsCustom && f.Section == "ek");

        var contract = await admin.PostAsJsonAsync("/api/contracts/contracts",
            new SaveContractRequest("Özel alanlı", s.PartyId, Custom: new JsonObject { [field.Key] = "Gold" }));
        Assert.Equal(HttpStatusCode.Created, contract.StatusCode);
        var dto = (await contract.Content.ReadFromJsonAsync<ContractDto>())!;
        Assert.Equal("Gold", dto.Custom![field.Key]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_users_with_customization_permission_can_change_the_contract_layout()
    {
        var s = await SetUpAsync();
        using var reader = await LoginAsync(s.Reader);
        using var admin = await LoginAsync(s.Admin);

        // Okuyucu düzeni görür (formu çizmek için) ama değiştiremez.
        var layout = await LayoutAsync(reader);
        var request = new SaveFormLayoutRequest(layout.Tabs, layout.Sections,
            [.. layout.Fields.Select(f => new SaveFormFieldRequest(f.Key, f.Section, f.Span, f.Required, f.Visible))], layout.Version);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/customization/form-layouts/{EntityKey}", request)).StatusCode);
        Assert.False((await LayoutAsync(admin)).Customized);
    }

    private static async Task<FormLayoutDto> LayoutAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<FormLayoutDto>>("/api/form-layouts"))!.Single(l => l.EntityKey == EntityKey);

    private async Task<(ModulixUser Admin, ModulixUser Reader, Guid PartyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var response = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject { ["name"] = "Anka Lojistik A.Ş." }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var party = (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
        return (admin, reader, party.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
