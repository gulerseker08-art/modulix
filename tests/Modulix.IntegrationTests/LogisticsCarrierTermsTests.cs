using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 3b: taşıyıcı anlaşması. Mutlu yol (anlaşma, birbirini izleyen dönemler, "bugün geçerli" ve "süresi dolan" süzgeçleri,
/// taşıyıcı adına göre arama ve sıralama, denetim kaydı, düzeltme için silme), doğrulama (taşıyıcı rolü, tarih sırası, para birimi,
/// numara benzersizliği, etkin anlaşmaların çakışması; pasif anlaşma çakışmaz), tenant izolasyonu ve yetki (taşıyıcı izinleri, sürücü
/// izni anlaşmaları açmaz).
/// </summary>
public class LogisticsCarrierTermsTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Terms = "/api/records/logistics.carrier-terms";
    private const string Parties = "/api/records/core.party";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Successive_agreements_are_listed_and_filtered_by_their_validity()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var kartal = await PartyAsync(admin, "Kartal Nakliyat", "tasiyici");
        var anadolu = await PartyAsync(admin, "Anadolu Lojistik", "tasiyici");

        var old = await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = kartal.ToString(), ["title"] = "2025 yurt içi", ["agreementNo"] = "kn-2025",
            ["validFrom"] = Iso(Today.AddYears(-1)), ["validTo"] = Iso(Today.AddDays(-1)), ["currency"] = "try",
        });
        Assert.Equal("KN-2025", old.Values["agreementNo"]!.GetValue<string>());
        Assert.Equal("TRY", old.Values["currency"]!.GetValue<string>());
        var current = await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = kartal.ToString(), ["title"] = "2026 yurt içi", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
            ["paymentTermDays"] = 45, ["rateNote"] = "Komple TIR İstanbul–Ankara 18.000 TRY",
        });
        var euro = await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = anadolu.ToString(), ["title"] = "Avrupa parsiyel", ["validFrom"] = Iso(Today.AddDays(-10)),
            ["validTo"] = Iso(Today.AddDays(90)), ["currency"] = "EUR",
        });

        Assert.Equal([euro.Id, current.Id], (await ListAsync(admin, $"validOn={Iso(Today)}&sort=carrier")).Select(t => t.Id)); // Anadolu, Kartal
        Assert.Equal([old.Id], (await ListAsync(admin, $"expiredBefore={Iso(Today)}")).Select(t => t.Id));
        Assert.Equal([current.Id, old.Id], (await ListAsync(admin, $"carrierPartyId={kartal}")).Select(t => t.Id)); // en yeni dönem üstte
        Assert.Equal(2, (await ListAsync(admin, "search=Kartal")).Count);

        var update = await admin.PutAsJsonAsync($"{Terms}/{current.Id}",
            new RecordWriteRequest(new JsonObject { ["performanceNote"] = "Ekim'de iki gecikme" }, current.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Terms}/{current.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "performanceNote"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Terms}/{euro.Id}?version={euro.Version}")).StatusCode);
    }

    [Fact]
    public async Task Overlapping_or_inconsistent_agreements_are_rejected_but_a_passive_one_may_overlap()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var carrier = await PartyAsync(admin, "Hızlı Taşıma", "tasiyici");
        var customer = await PartyAsync(admin, "Müşteri A.Ş.", "musteri");
        await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = carrier.ToString(), ["title"] = "Ana anlaşma", ["agreementNo"] = "HT-1", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
        });

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Terms, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("carrierPartyId"));
        Assert.True(missing.Errors.ContainsKey("title"));
        Assert.True(missing.Errors.ContainsKey("validFrom"));

        var notCarrier = await ValidationProblemAsync(await admin.PostAsJsonAsync(Terms, new RecordWriteRequest(new JsonObject
        {
            ["carrierPartyId"] = customer.ToString(), ["title"] = "Yanlış rol", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
        })));
        Assert.Contains("Taşıyıcı", notCarrier.Errors["carrierPartyId"].Single());

        var inconsistent = await ValidationProblemAsync(await admin.PostAsJsonAsync(Terms, new RecordWriteRequest(new JsonObject
        {
            ["carrierPartyId"] = carrier.ToString(), ["title"] = "Ters tarih", ["agreementNo"] = "ht-1",
            ["validFrom"] = Iso(Today.AddDays(10)), ["validTo"] = Iso(Today), ["currency"] = "TL",
        })));
        Assert.Equal(["Bitiş tarihi başlangıç tarihinden önce olamaz."], inconsistent.Errors["validTo"]);
        Assert.Equal(["Üç harfli para birimi girin (ör. TRY)."], inconsistent.Errors["currency"]);
        Assert.Equal(["Bu taşıyıcıda aynı numaralı başka bir anlaşma var."], inconsistent.Errors["agreementNo"]);

        // Süresiz etkin anlaşma varken gelecekteki bir dönem de çakışır; pasif anlaşma çakışma denetimine girmez.
        var overlap = await ValidationProblemAsync(await admin.PostAsJsonAsync(Terms, new RecordWriteRequest(new JsonObject
        {
            ["carrierPartyId"] = carrier.ToString(), ["title"] = "Gelecek dönem", ["validFrom"] = Iso(Today.AddMonths(6)), ["currency"] = "TRY",
        })));
        Assert.Contains("Ana anlaşma", overlap.Errors["validFrom"].Single());
        await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = carrier.ToString(), ["title"] = "Taslak teklif", ["validFrom"] = Iso(Today.AddMonths(6)), ["currency"] = "TRY", ["isActive"] = false,
        });
    }

    [Fact]
    public async Task Another_tenants_carriers_and_agreements_stay_invisible()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var carrierA = await PartyAsync(adminA, "A Taşıyıcı", "tasiyici");
        var termsA = await PostAsync(adminA, Terms, new JsonObject
        {
            ["carrierPartyId"] = carrierA.ToString(), ["title"] = "A anlaşması", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
        });

        var foreign = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Terms, new RecordWriteRequest(new JsonObject
        {
            ["carrierPartyId"] = carrierA.ToString(), ["title"] = "Yabancı", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
        })));
        Assert.Equal(["Taşıyıcı firma bulunamadı."], foreign.Errors["carrierPartyId"]);
        Assert.Empty(await ListAsync(adminB, null));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Terms}/{termsA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Terms}/{termsA.Id}?version={termsA.Version}")).StatusCode);
    }

    [Fact]
    public async Task Carrier_permissions_guard_agreements_and_driver_access_does_not_open_them()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var driversOnly = await LoginAsync(s.DriversOnly);
        var carrier = await PartyAsync(admin, "Yetki Taşıma", "tasiyici");
        var terms = await PostAsync(admin, Terms, new JsonObject
        {
            ["carrierPartyId"] = carrier.ToString(), ["title"] = "Yetki", ["validFrom"] = Iso(Today), ["currency"] = "TRY",
        });

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Terms}/{terms.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Terms}/{terms.Id}",
            new RecordWriteRequest(new JsonObject { ["title"] = "Değişti" }, terms.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Terms}/{terms.Id}?version={terms.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driversOnly.GetAsync(Terms)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Reader, ModulixUser DriversOnly);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // taşıyıcı firmayı kayıt ucundan açmak için (firma yazma izni CRM ile gelir)
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var driversOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [CarriersRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [DriversRead, DriversWrite], Scope.All, driversOnly);
        return new Setup(tenant.Id, admin, reader, driversOnly);
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

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? Terms : $"{Terms}?{query}"))!.Items;

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
