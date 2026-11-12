using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Files;
using Modulix.Shared.Records;
using static Modulix.Modules.Fleet.FleetModule.Permissions;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 2a: Core süreli belge. Mutlu yol (araca ve sürücüye belge, bitişe göre sıra, geçerlilik ve bitiş süzgeçleri, dosya eki, denetim,
/// dosyası olan belge silinmez), doğrulama (sahip türü, kategori, tarih sırası, numara tekilliği, sahip değişmez), tenant izolasyonu (başka tenant'ın
/// kaydına belge eklenemez, belgesi görünmez) ve yetki (belgeye erişim sahip kaydın erişimidir: araç okuyan araç belgesini görür ama değiştiremez,
/// sürücü izni araç belgelerini göstermez, izinsiz göremez).
/// </summary>
public class CredentialTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Credentials = "/api/records/core.credential";
    private const string Vehicles = "/api/records/fleet.vehicle";
    private const string Drivers = "/api/records/logistics.driver";
    private const string People = "/api/records/core.person";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");

    [Fact]
    public async Task Vehicles_and_drivers_carry_dated_credentials_with_files_ordered_by_expiry()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BLG01", ["kindKey"] = "Truck" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var inspection = await PostAsync(admin, Credentials, Credential("fleet.vehicle", truck.Id, "Inspection", "MUA-1", today.AddYears(-1), today.AddDays(20)));
        var insurance = await PostAsync(admin, Credentials, Credential("fleet.vehicle", truck.Id, "TrafficInsurance", "POL-9", today.AddMonths(-11), today.AddDays(-3)));
        var casco = await PostAsync(admin, Credentials, Credential("fleet.vehicle", truck.Id, "Casco", null, null, null));
        Assert.Equal("active", inspection.Values["status"]!.GetValue<string>());

        // Bitişi en yakın önce; süresiz belge sonda.
        Assert.Equal([insurance.Id, inspection.Id, casco.Id], (await ListAsync(admin, $"ownerEntityKey=fleet.vehicle&ownerId={truck.Id}")).Select(c => c.Id));
        Assert.Equal([inspection.Id, casco.Id], (await ListAsync(admin, $"ownerId={truck.Id}&validOn={today:yyyy-MM-dd}")).Select(c => c.Id));
        Assert.Equal([insurance.Id, inspection.Id], (await ListAsync(admin, $"ownerId={truck.Id}&expiresBy={today.AddDays(30):yyyy-MM-dd}")).Select(c => c.Id));
        Assert.Equal([insurance.Id], (await ListAsync(admin, $"ownerId={truck.Id}&expiredBefore={today:yyyy-MM-dd}")).Select(c => c.Id));
        Assert.Equal([inspection.Id], (await ListAsync(admin, "search=MUA")).Select(c => c.Id));

        // Sürücünün yetkinlikleri de aynı belge kaydıdır.
        var person = (await PostAsync(admin, People, new JsonObject { ["firstName"] = "Hasan", ["lastName"] = "Öz" })).Id;
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.ToString() });
        var src = await PostAsync(admin, Credentials, Credential("logistics.driver", driver.Id, "Src", "SRC-4", today.AddYears(-2), today.AddYears(3)));
        Assert.Equal([src.Id], (await ListAsync(admin, $"ownerEntityKey=logistics.driver")).Select(c => c.Id));

        // İptal edilen belge geçerli sayılmaz.
        var revoke = await admin.PutAsJsonAsync($"{Credentials}/{src.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "revoked" }, src.Version));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Empty(await ListAsync(admin, $"ownerId={driver.Id}&validOn={today:yyyy-MM-dd}"));
        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Credentials}/{src.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));

        // Belgenin dosyası Core dosya deposundadır; dosyası olan belge silinmez.
        using (var form = Form(inspection.Id, "Muayene raporu.pdf"))
        {
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/files", form)).StatusCode);
        }

        var files = (await admin.GetFromJsonAsync<List<StoredFileDto>>($"/api/files?entityKey=core.credential&recordId={inspection.Id}"))!;
        Assert.Equal("Muayene raporu.pdf", files.Single().FileName);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Credentials}/{inspection.Id}?version={inspection.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/files/{files[0].Id}?version={files[0].Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Credentials}/{inspection.Id}?version={inspection.Version}")).StatusCode);
    }

    [Fact]
    public async Task Credentials_must_match_the_owner_type_dates_and_numbers()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34DGR01", ["kindKey"] = "Truck" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("ownerEntityKey") && missing.Errors.ContainsKey("ownerId") && missing.Errors.ContainsKey("typeKey"));

        var notOwner = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials, new RecordWriteRequest(Credential("core.person", Guid.NewGuid(), "Src", null, null, null))));
        Assert.Equal(["Bu kayıt türüne süreli belge eklenemez."], notOwner.Errors["ownerEntityKey"]);
        var unknownVehicle = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials, new RecordWriteRequest(Credential("fleet.vehicle", Guid.NewGuid(), "Inspection", null, null, null))));
        Assert.Equal(["Kayıt bulunamadı."], unknownVehicle.Errors["ownerId"]);

        // Sürücü belgesi araca eklenemez; listede olmayan tür reddedilir.
        var wrongCategory = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials, new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Src", null, null, null))));
        Assert.Equal(["Bu belge türü araç kaydına eklenemez."], wrongCategory.Errors["typeKey"]);
        var unknownType = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials, new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Yok", null, null, null))));
        Assert.Equal(["Listede olmayan değer."], unknownType.Errors["typeKey"]);

        var backwards = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Inspection", null, today, today.AddDays(-1)))));
        Assert.Equal(["Bitiş tarihi başlangıçtan önce olamaz."], backwards.Errors["validTo"]);

        var first = await PostAsync(admin, Credentials, Credential("fleet.vehicle", truck.Id, "Inspection", "A-1", null, today.AddYears(1)));
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Inspection", "A-1", null, null))));
        Assert.Equal(["Bu kayıtta aynı türde ve numarada bir belge zaten var."], duplicate.Errors["documentNo"]);

        // Sahip oluşturulduktan sonra değişmez.
        var other = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34DGR02", ["kindKey"] = "Truck" });
        var moved = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Credentials}/{first.Id}",
            new RecordWriteRequest(new JsonObject { ["ownerId"] = other.Id.ToString() }, first.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], moved.Errors["ownerId"]);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Credentials}/{first.Id}",
            new RecordWriteRequest(new JsonObject { ["documentNo"] = "A-2" }, first.Version + 1))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_records_and_credentials_cannot_be_used_or_seen()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var truckA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "06IZO01", ["kindKey"] = "Truck" });
        var credentialA = await PostAsync(adminA, Credentials, Credential("fleet.vehicle", truckA.Id, "Inspection", "ISO-1", null, null));

        var foreignOwner = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truckA.Id, "Inspection", null, null, null))));
        Assert.Equal(["Kayıt bulunamadı."], foreignOwner.Errors["ownerId"]);

        Assert.Empty(await ListAsync(adminB, null));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Credentials}/{credentialA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Credentials}/{credentialA.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "revoked" }, credentialA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Credentials}/{credentialA.Id}?version={credentialA.Version}")).StatusCode);
        using var form = Form(credentialA.Id, "Sızma.pdf");
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync("/api/files", form)).StatusCode);
    }

    [Fact]
    public async Task Credential_access_follows_the_owner_record_access()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var vehicleReader = await LoginAsync(s.VehicleReader);
        using var driverWriter = await LoginAsync(s.DriverWriter);
        using var outsider = await LoginAsync(s.Outsider);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34YTK01", ["kindKey"] = "Truck" });
        var inspection = await PostAsync(admin, Credentials, Credential("fleet.vehicle", truck.Id, "Inspection", "YTK-1", null, null));
        var person = (await PostAsync(admin, People, new JsonObject { ["firstName"] = "Eda", ["lastName"] = "Kurt" })).Id;
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.ToString() });

        // Araç okuyan araç belgesini görür ama değiştiremez, belge ya da dosya ekleyemez.
        Assert.Equal([inspection.Id], (await ListAsync(vehicleReader, null)).Select(c => c.Id));
        Assert.Equal(HttpStatusCode.OK, (await vehicleReader.GetAsync($"{Credentials}/{inspection.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vehicleReader.PutAsJsonAsync($"{Credentials}/{inspection.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "revoked" }, inspection.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vehicleReader.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Casco", null, null, null)))).StatusCode);
        using (var form = Form(inspection.Id, "Okur.pdf"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await vehicleReader.PostAsync("/api/files", form)).StatusCode);
        }

        // Sürücü yazan sürücüye belge ekler; araç belgelerini görmez, araca belge ekleyemez.
        var src = await PostAsync(driverWriter, Credentials, Credential("logistics.driver", driver.Id, "Src", null, null, null));
        Assert.Equal([src.Id], (await ListAsync(driverWriter, null)).Select(c => c.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await driverWriter.GetAsync($"{Credentials}/{inspection.Id}")).StatusCode);
        var vehicleByDriverWriter = await ValidationProblemAsync(await driverWriter.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Casco", null, null, null))));
        Assert.Equal(["Bu kayıt türüne süreli belge eklenemez."], vehicleByDriverWriter.Errors["ownerEntityKey"]);

        // Araç yazan sürücü belgesini görür ama değiştiremez, dosyasını ekleyemez (yazma sahip türüne göre denetlenir).
        using var vehicleWriter = await LoginAsync(s.VehicleWriter);
        Assert.Equal(HttpStatusCode.OK, (await vehicleWriter.GetAsync($"{Credentials}/{src.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vehicleWriter.PutAsJsonAsync($"{Credentials}/{src.Id}",
            new RecordWriteRequest(new JsonObject { ["documentNo"] = "X" }, src.Version))).StatusCode);
        using (var form = Form(src.Id, "Sürücü.pdf"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await vehicleWriter.PostAsync("/api/files", form)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await vehicleWriter.PostAsJsonAsync(Credentials,
            new RecordWriteRequest(Credential("fleet.vehicle", truck.Id, "Casco", null, null, null)))).StatusCode);

        // Sahip modülü kapanınca belgeleri de görünmez.
        await factory.SetModuleAsync(s.TenantId, "fleet", enabled: false);
        Assert.Equal([src.Id], (await ListAsync(admin, null)).Select(c => c.Id)); // araç belgeleri (muayene, kasko) gizlendi
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Credentials)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser VehicleReader, ModulixUser DriverWriter, ModulixUser VehicleWriter, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var vehicleReader = await factory.CreateUserAsync(tenant);
        var driverWriter = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        var vehicleWriter = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [VehiclesRead], Scope.All, vehicleReader);
        await factory.CreateRoleAsync(tenant.Id, [DriversRead, DriversWrite], Scope.All, driverWriter);
        await factory.CreateRoleAsync(tenant.Id, [VehiclesRead, VehiclesWrite, DriversRead], Scope.All, vehicleWriter);
        return new Setup(tenant.Id, admin, vehicleReader, driverWriter, vehicleWriter, outsider);
    }

    private static JsonObject Credential(string ownerKey, Guid ownerId, string type, string? no, DateOnly? from, DateOnly? to) => new()
    {
        ["ownerEntityKey"] = ownerKey,
        ["ownerId"] = ownerId.ToString(),
        ["typeKey"] = type,
        ["documentNo"] = no,
        ["validFrom"] = from?.ToString("yyyy-MM-dd"),
        ["validTo"] = to?.ToString("yyyy-MM-dd"),
    };

    private static MultipartFormDataContent Form(Guid credentialId, string fileName)
    {
        var file = new ByteArrayContent([.. Pdf, .. Encoding.ASCII.GetBytes(Guid.NewGuid().ToString())]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent("core.credential"), "entityKey" },
            { new StringContent(credentialId.ToString()), "recordId" },
            { file, "file", fileName },
        };
    }

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? Credentials : $"{Credentials}?{query}"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
