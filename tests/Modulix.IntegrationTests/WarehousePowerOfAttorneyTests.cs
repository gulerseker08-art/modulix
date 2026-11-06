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
/// ADR-0032 dilim 3 (vekâlet, K4): mutlu yol (oluşturma, süzme, güncelleme, iptal, silme), doğrulama (aynı firma, rol, tarih, taraflar
/// sonradan değişmez), firma seçiminin role göre süzülmesi, tenant izolasyonu (başka tenant'ın firması/vekâleti) ve yetki.
/// </summary>
public class WarehousePowerOfAttorneyTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Attorneys = "/api/records/warehouse.power-of-attorney";
    private const string Parties = "/api/records/core.party";

    [Fact]
    public async Task Attorney_is_created_filtered_updated_revoked_and_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "Mersin İthalat A.Ş.", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Liman Müşavirlik Ltd.", "gumruk-musaviri");

        var attorney = await PostAsync(admin, Attorneys, new JsonObject
        {
            ["importerPartyId"] = importer.ToString(), ["brokerPartyId"] = broker.ToString(), ["validFrom"] = "2026-01-01", ["validTo"] = "2026-12-31",
            ["scope"] = "İthalat beyannameleri", ["documentReference"] = "Noter 12345",
        });
        Assert.True(attorney.Values["isActive"]!.GetValue<bool>());
        Assert.Equal("2026-12-31", attorney.Values["validTo"]!.GetValue<string>());

        // Süzgeçler: ithalatçı, müşavir ve belirli günde geçerlilik. Arama kapsam ve belge referansında çalışır.
        Assert.Equal([attorney.Id], (await ListAsync(admin, $"importerPartyId={importer}")).Select(i => i.Id));
        Assert.Equal([attorney.Id], (await ListAsync(admin, $"brokerPartyId={broker}")).Select(i => i.Id));
        Assert.Equal([attorney.Id], (await ListAsync(admin, "validOn=2026-06-15")).Select(i => i.Id));
        Assert.Empty(await ListAsync(admin, "validOn=2027-01-01"));
        Assert.Empty(await ListAsync(admin, "validOn=2025-12-31"));
        Assert.Equal([attorney.Id], (await ListAsync(admin, "search=12345")).Select(i => i.Id));

        // Süresiz yapılabilir (bitiş boş); sonra iptal edilince tarihi geçerli olsa da "geçerli" sayılmaz ama kayıt kalır.
        var open = await admin.PutAsJsonAsync($"{Attorneys}/{attorney.Id}", new RecordWriteRequest(new JsonObject { ["validTo"] = null }, attorney.Version));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var opened = (await open.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Null(opened.Values["validTo"]);
        Assert.Equal([attorney.Id], (await ListAsync(admin, "validOn=2035-01-01")).Select(i => i.Id));

        var revoke = await admin.PutAsJsonAsync($"{Attorneys}/{attorney.Id}", new RecordWriteRequest(new JsonObject { ["isActive"] = false }, opened.Version));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revoked = (await revoke.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Empty(await ListAsync(admin, "validOn=2026-06-15"));
        Assert.Equal([attorney.Id], (await ListAsync(admin, "isActive=false")).Select(i => i.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Attorneys}/{attorney.Id}?version={revoked.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Attorneys}/{attorney.Id}")).StatusCode);
    }

    [Fact]
    public async Task Attorney_roles_dates_and_parties_are_validated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "İthalatçı Firma", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Müşavir Firma", "gumruk-musaviri");
        var both = await PartyAsync(admin, "Hem İthalatçı Hem Müşavir", "ithalatci-malik", "gumruk-musaviri");
        var plain = await PartyAsync(admin, "Rolsüz Firma");

        JsonObject Body(Guid importerId, Guid brokerId, string from = "2026-01-01", string? to = null) => new()
        {
            ["importerPartyId"] = importerId.ToString(), ["brokerPartyId"] = brokerId.ToString(), ["validFrom"] = from, ["validTo"] = to,
        };

        // Aynı firma hem ithalatçı hem müşavir olarak seçilemez (iki rolü olsa bile).
        var same = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(both, both))));
        Assert.Equal(["İthalatçı ve gümrük müşaviri aynı firma olamaz."], same.Errors["brokerPartyId"]);

        // Rol denetimi: ithalatçı alanında yalnızca müşavir rolündeki firma, müşavir alanında rolsüz firma reddedilir.
        var wrongImporter = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(broker, both))));
        Assert.Contains("\"İthalatçı / malik\" ilişki rolüne sahip değil", wrongImporter.Errors["importerPartyId"][0]);
        var wrongBroker = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(importer, plain))));
        Assert.Contains("\"Gümrük müşaviri\" ilişki rolüne sahip değil", wrongBroker.Errors["brokerPartyId"][0]);

        // İki rolü olan firma, farklı bir firmayla eşleşince her iki tarafta da kullanılabilir.
        await PostAsync(admin, Attorneys, Body(both, broker));
        await PostAsync(admin, Attorneys, Body(importer, both));

        var badDates = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(importer, broker, "2026-06-01", "2026-05-31"))));
        Assert.Equal(["Bitiş tarihi başlangıç tarihinden önce olamaz."], badDates.Errors["validTo"]);
        var badFormat = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(importer, broker, "01.06.2026"))));
        Assert.Contains("validFrom", badFormat.Errors.Keys);
        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(new JsonObject { ["scope"] = "x" })));
        Assert.Contains("importerPartyId", missing.Errors.Keys);
        Assert.Contains("brokerPartyId", missing.Errors.Keys);
        Assert.Contains("validFrom", missing.Errors.Keys);
        var unknown = await ValidationProblemAsync(await admin.PostAsJsonAsync(Attorneys, new RecordWriteRequest(Body(Guid.NewGuid(), broker))));
        Assert.Equal(["İthalatçı bulunamadı."], unknown.Errors["importerPartyId"]);

        // Taraflar sonradan değişmez; bitiş tarihi sonradan başlangıçtan önceye çekilemez.
        var attorney = await PostAsync(admin, Attorneys, Body(importer, broker, "2026-03-01", "2026-09-01"));
        var moved = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Attorneys}/{attorney.Id}",
            new RecordWriteRequest(new JsonObject { ["brokerPartyId"] = both.ToString() }, attorney.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], moved.Errors["brokerPartyId"]);
        var earlier = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Attorneys}/{attorney.Id}",
            new RecordWriteRequest(new JsonObject { ["validTo"] = "2026-02-01" }, attorney.Version)));
        Assert.Equal(["Bitiş tarihi başlangıç tarihinden önce olamaz."], earlier.Errors["validTo"]);

        // Vekâleti olan firma silinemez (ortak kayıt kullanım koruması).
        var party = (await admin.GetFromJsonAsync<RecordResponse>($"{Parties}/{importer}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Parties}/{importer}?version={party.Version}")).StatusCode);
    }

    [Fact]
    public async Task Party_lookups_only_offer_parties_with_the_matching_relationship_role()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "Seçim İthalatçı", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Seçim Müşavir", "gumruk-musaviri");
        var other = await PartyAsync(admin, "Seçim Tedarikçi", "tedarikci");

        var lookups = (await admin.GetFromJsonAsync<List<LookupDto>>("/api/lookups"))!;
        Assert.Equal(WarehouseAccess.ModuleKey, Assert.Single(lookups, l => l.Key == WarehouseAccess.AttorneyImporterLookup).ModuleKey);

        var importers = await SearchAsync(admin, WarehouseAccess.AttorneyImporterLookup, "seçim");
        Assert.Equal([importer], importers.Items.Select(i => i.Id));
        var brokers = await SearchAsync(admin, WarehouseAccess.AttorneyBrokerLookup, "seçim");
        Assert.Equal([broker], brokers.Items.Select(i => i.Id));
        Assert.DoesNotContain(importers.Items.Concat(brokers.Items), i => i.Id == other);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_use_each_others_attorneys_and_parties()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var importerA = await PartyAsync(adminA, "A İthalatçı", "ithalatci-malik");
        var brokerA = await PartyAsync(adminA, "A Müşavir", "gumruk-musaviri");
        var attorneyA = await PostAsync(adminA, Attorneys, new JsonObject
        {
            ["importerPartyId"] = importerA.ToString(), ["brokerPartyId"] = brokerA.ToString(), ["validFrom"] = "2026-01-01",
        });
        var brokerB = await PartyAsync(adminB, "B Müşavir", "gumruk-musaviri");

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Attorneys}/{attorneyA.Id}")).StatusCode);
        Assert.DoesNotContain(await ListAsync(adminB, null), i => i.Id == attorneyA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Attorneys}/{attorneyA.Id}?version={attorneyA.Version}")).StatusCode);

        // B, A'nın firmasıyla vekâlet kuramaz; varlığı da belli edilmez. Seçim kutusu da A'nın firmasını getirmez.
        var foreign = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Attorneys, new RecordWriteRequest(new JsonObject
        {
            ["importerPartyId"] = importerA.ToString(), ["brokerPartyId"] = brokerB.ToString(), ["validFrom"] = "2026-01-01",
        })));
        Assert.Equal(["İthalatçı bulunamadı."], foreign.Errors["importerPartyId"]);
        Assert.Empty((await SearchAsync(adminB, WarehouseAccess.AttorneyImporterLookup, "ithalatçı")).Items);
    }

    [Fact]
    public async Task Attorneys_need_their_own_permission_and_party_lookups_follow_the_attorney_read_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var facilityOnly = await LoginAsync(s.FacilityOnly);
        var importer = await PartyAsync(admin, "Yetki İthalatçı", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Yetki Müşavir", "gumruk-musaviri");
        var attorney = await PostAsync(admin, Attorneys, new JsonObject
        {
            ["importerPartyId"] = importer.ToString(), ["brokerPartyId"] = broker.ToString(), ["validFrom"] = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Attorneys}/{attorney.Id}")).StatusCode);
        var body = new RecordWriteRequest(new JsonObject
        {
            ["importerPartyId"] = importer.ToString(), ["brokerPartyId"] = broker.ToString(), ["validFrom"] = "2026-02-01",
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Attorneys, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Attorneys}/{attorney.Id}?version={attorney.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Attorneys, body)).StatusCode);

        // Okuma izni olan firmaları seçebilir; vekâlet izni olmayan (yalnızca tesis izni) vekâleti göremez.
        Assert.Contains((await SearchAsync(reader, WarehouseAccess.AttorneyImporterLookup, "yetki")).Items, i => i.Id == importer);
        Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync(Attorneys)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await facilityOnly.GetAsync("/api/records/warehouse.facility")).StatusCode);
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser FacilityOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
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

        await factory.CreateRoleAsync(tenant.Id, [AttorneysRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [AttorneysRead, AttorneysWrite], Scope.All, writer);
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

    private static async Task<Guid> PartyAsync(HttpClient client, string name, params string[] roles) =>
        (await PostAsync(client, Parties, new JsonObject
        {
            ["name"] = name,
            ["taxNumber"] = Guid.NewGuid().ToString("N")[..10],
            ["relationshipRoles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        })).Id;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query)
    {
        var response = await client.GetAsync($"{Attorneys}?pageSize=50{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items;
    }

    private static async Task<LookupPageDto> SearchAsync(HttpClient client, string key, string text)
    {
        var response = await client.GetAsync($"/api/lookups/{key}/search?page=1&q={Uri.EscapeDataString(text)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LookupPageDto>())!;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
