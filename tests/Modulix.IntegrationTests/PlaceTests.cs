using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0045 dilim 3a: yer defteri (<c>core.place</c>, Core ortak kaydı). Mutlu yol (ekle, ülke koduna büyük harf, süzgeç ve arama, pasife alma, silme),
/// doğrulama (zorunlu ad, geçersiz ülke, yarım koordinat, aralık dışı koordinat, listede olmayan tür, ülke içinde tekil ad, eski sürüm),
/// tenant izolasyonu (başka tenant'ın yeri görünmez ve değiştirilemez, aynı ad serbest) ve yetki (okuyan yazamaz, izinsiz 403, modül kapalıyken 403).
/// </summary>
public class PlaceTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Places = "/api/records/core.place";

    [Fact]
    public async Task Places_can_be_created_found_deactivated_and_deleted()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        var depot = await PostAsync(manager, Place("İstanbul Depo", "tr", "Depot", 41.0082m, 28.9784m));
        Assert.Equal(("İstanbul Depo", "TR", "Depot", true), (Text(depot, "name"), Text(depot, "countryCode"), Text(depot, "kindKey"), depot.Values["isActive"]!.GetValue<bool>()));
        Assert.Equal(41.0082m, depot.Values["latitude"]!.GetValue<decimal>());
        var port = await PostAsync(manager, Place("Hamburg Limanı", "DE", "Port", null, null));
        Assert.Equal("Port", Text(port, "kindKey"));

        // Süzgeç ve arama (arama aksan ve büyük/küçük harfe duyarsızdır).
        Assert.Equal([port.Id], (await ListAsync(manager, "countryCode=de")).Select(r => r.Id));
        Assert.Equal([depot.Id], (await ListAsync(manager, "kindKey=Depot")).Select(r => r.Id));
        Assert.Equal([depot.Id], (await ListAsync(manager, "search=ISTANBUL")).Select(r => r.Id));

        // Pasife alma: etkin süzgecinden çıkar, pasif süzgecinde görünür.
        var passive = await PutAsync(manager, depot, new JsonObject { ["isActive"] = false });
        Assert.False(passive.Values["isActive"]!.GetValue<bool>());
        Assert.Equal([port.Id], (await ListAsync(manager, "isActive=true")).Select(r => r.Id));
        Assert.Equal([depot.Id], (await ListAsync(manager, "isActive=false")).Select(r => r.Id));

        // Kullanılmayan yer silinebilir.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"{Places}/{port.Id}?version={port.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"{Places}/{port.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_places_are_rejected_and_names_are_unique_per_country()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(new JsonObject())))).Errors.ContainsKey("name"));
        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("X", "ZZ", "Depot", null, null))))).Errors.ContainsKey("countryCode"));
        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("X", "TR", "Depot", 41m, null))))).Errors.ContainsKey("longitude"));
        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("X", "TR", "Depot", null, 28m))))).Errors.ContainsKey("latitude"));
        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("X", "TR", "Depot", 91m, 28m))))).Errors.ContainsKey("latitude"));
        Assert.True((await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("X", "TR", "Teleport", null, null))))).Errors.ContainsKey("kindKey"));

        // Tür verilmezse "Diğer" olur.
        var untyped = Place("Kapıkule", "TR", "Depot", null, null);
        untyped.Remove("kindKey");
        var created = await PostAsync(manager, untyped);
        Assert.Equal("Other", Text(created, "kindKey"));

        // Ad ülke içinde tekildir (aksan, harf ve boşluk farkı yok sayılır); başka ülkede aynı ad serbesttir.
        Assert.Equal(["Bu ülkede aynı adlı bir yer zaten var."],
            (await ValidationProblemAsync(await manager.PostAsJsonAsync(Places, new RecordWriteRequest(Place("  KAPIKULE ", "TR", "Depot", null, null))))).Errors["name"]);
        await PostAsync(manager, Place("Kapıkule", "BG", "Depot", null, null));

        // Eski sürümle güncelleme çakışmadır.
        await PutAsync(manager, created, new JsonObject { ["city"] = "Edirne" });
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Places}/{created.Id}",
            new RecordWriteRequest(new JsonObject { ["city"] = "Kırklareli" }, created.Version))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_places_are_invisible_and_cannot_be_changed()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        var placeA = await PostAsync(managerA, Place("Ortak Ad Deposu", "TR", "Depot", null, null));

        Assert.Empty(await ListAsync(managerB, "search=Ortak"));
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Places}/{placeA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PutAsJsonAsync($"{Places}/{placeA.Id}",
            new RecordWriteRequest(new JsonObject { ["city"] = "Ankara" }, placeA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"{Places}/{placeA.Id}?version={placeA.Version}")).StatusCode);

        // Aynı ad B tenant'ında serbesttir; A'nın kaydı değişmedi.
        await PostAsync(managerB, Place("Ortak Ad Deposu", "TR", "Depot", null, null));
        Assert.Null((await GetAsync(managerA, placeA.Id)).Values["city"]);
    }

    [Fact]
    public async Task Readers_see_places_but_cannot_write_and_outsiders_or_a_closed_module_get_403()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        using var outsider = await LoginAsync(s.Outsider);
        var place = await PostAsync(manager, Place("Mersin Limanı", "TR", "Port", null, null));

        Assert.Equal([place.Id], (await ListAsync(reader, "search=Mersin")).Select(r => r.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Places, new RecordWriteRequest(Place("Yeni", "TR", "Depot", null, null)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Places}/{place.Id}", new RecordWriteRequest(new JsonObject { ["city"] = "X" }, place.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Places}/{place.Id}?version={place.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Places)).StatusCode);

        // Lojistik kapanınca yer defterini okuyan izin kalmaz.
        await factory.SetModuleAsync(s.TenantId, "logistics", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(Places)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Manager, ModulixUser Reader, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ShipmentsRead, PlacesWrite], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [ShipmentsRead], Scope.All, reader);
        return new Setup(tenant.Id, manager, reader, outsider);
    }

    private static JsonObject Place(string name, string country, string kind, decimal? latitude, decimal? longitude) => new()
    {
        ["name"] = name,
        ["countryCode"] = country,
        ["kindKey"] = kind,
        ["latitude"] = latitude,
        ["longitude"] = longitude,
    };

    private static string Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>() ?? string.Empty;

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>($"{Places}?{query}"))!.Items;

    private static async Task<RecordResponse> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<RecordResponse>($"{Places}/{id}"))!;

    private static async Task<RecordResponse> PostAsync(HttpClient client, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(Places, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, RecordResponse record, JsonObject values)
    {
        var response = await client.PutAsJsonAsync($"{Places}/{record.Id}", new RecordWriteRequest(values, record.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
