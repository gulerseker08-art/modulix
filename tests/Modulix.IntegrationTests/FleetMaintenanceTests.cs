using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;
using static Modulix.Modules.Fleet.FleetModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 1b: bakım kaydı. Mutlu yol (kayıt, aracın türetilmiş bakım alanları, düzeltme ve silmede yeniden hesap, denetim kaydı,
/// "bakımı yaklaşan" süzgeci, "sonraki bakıma N gün kala" kuralının taramayla tetiklenmesi), doğrulama (tarih/kilometre tutarlılığı, para
/// birimi, araç değiştirilemez), tenant izolasyonu (başka tenant'ın aracına bakım yazılamaz, kayıtlar görünmez, tarama yalnızca kendi
/// araçlarını bulur) ve yetki (okuyan yazamaz, izinsiz göremez, araç izni bakım izni değildir).
/// </summary>
public class FleetMaintenanceTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private const string Vehicles = "/api/records/fleet.vehicle";
    private const string Maintenance = "/api/records/fleet.maintenance";

    private DateOnly Today => DateOnly.FromDateTime(fixture.Time.GetUtcNow().UtcDateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Maintenance_records_drive_the_vehicles_next_maintenance_and_corrections_recalculate_it()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var vehicle = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM01" });

        var older = await PostAsync(admin, Maintenance, new JsonObject
        {
            ["vehicleId"] = vehicle.Id.ToString(), ["kindKey"] = "Periodic", ["performedOn"] = Iso(Today.AddDays(-120)), ["odometerKm"] = 150_000,
            ["nextDueDate"] = Iso(Today.AddDays(-10)), ["nextDueKm"] = 180_000,
        });
        var latest = await PostAsync(admin, Maintenance, new JsonObject
        {
            ["vehicleId"] = vehicle.Id.ToString(), ["kindKey"] = "Repair", ["performedOn"] = Iso(Today.AddDays(-5)), ["odometerKm"] = 176_500,
            ["cost"] = 12_500.75m, ["currency"] = "try", ["nextDueDate"] = Iso(Today.AddDays(60)), ["nextDueKm"] = 200_000,
        });
        Assert.Equal("TRY", latest.Values["currency"]!.GetValue<string>());

        var read = await GetAsync(admin, $"{Vehicles}/{vehicle.Id}");
        Assert.Equal(Iso(Today.AddDays(-5)), read.Values["lastMaintenanceOn"]!.GetValue<string>());
        Assert.Equal(176_500, read.Values["odometerKm"]!.GetValue<int>());
        Assert.Equal(Iso(Today.AddDays(60)), read.Values["nextMaintenanceDate"]!.GetValue<string>());
        Assert.Equal(200_000, read.Values["nextMaintenanceKm"]!.GetValue<int>());

        // Türetilmiş alanlar araç formundan yazılamaz (sessizce yok sayılır).
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Vehicles}/{vehicle.Id}",
            new RecordWriteRequest(new JsonObject { ["nextMaintenanceDate"] = "2000-01-01" }, read.Version))).StatusCode);
        Assert.Equal(Iso(Today.AddDays(60)), (await GetAsync(admin, $"{Vehicles}/{vehicle.Id}")).Values["nextMaintenanceDate"]!.GetValue<string>());

        // En yeni bakım silinince araç bir önceki bakıma döner; "bakımı yaklaşan" süzgeci gecikeni bulur.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Maintenance}/{latest.Id}?version={latest.Version}")).StatusCode);
        read = await GetAsync(admin, $"{Vehicles}/{vehicle.Id}");
        Assert.Equal(Iso(Today.AddDays(-10)), read.Values["nextMaintenanceDate"]!.GetValue<string>());
        Assert.Equal(150_000, read.Values["odometerKm"]!.GetValue<int>());
        var due = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Vehicles}?maintenanceDueBy={Iso(Today.AddDays(30))}"))!;
        Assert.Equal([vehicle.Id], due.Items.Select(i => i.Id));

        var listed = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Maintenance}?vehicleId={vehicle.Id}"))!;
        Assert.Equal([older.Id], listed.Items.Select(i => i.Id));
        var searched = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Maintenance}?search=34 bkm"))!;
        Assert.Equal([older.Id], searched.Items.Select(i => i.Id));
        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Maintenance}/{older.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Created && h.UserId == s.Admin.Id);
    }

    [Fact]
    public async Task A_rule_seven_days_before_the_next_maintenance_fires_only_for_the_matching_vehicle()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var soon = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM10" });
        var later = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM11" });
        await MaintainAsync(admin, soon.Id, Today.AddDays(7));
        await MaintainAsync(admin, later.Id, Today.AddDays(20));

        // Bakıma 7 gün kala kural: tarama Filo'nun kayıt kaynağından araçları bulur ("Bekle" adımı yan etkisiz bir akış başlatır).
        var rule = await PublishRuleAsync(admin, new AutomationDefinition(
            new DateRelativeTrigger("fleet.vehicle.nextMaintenanceDate", -7), null,
            [new AutomationActionStep("bekle", FlowAutomation.WaitAction, new JsonObject { ["days"] = 1 })]));

        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.True(round!.Scanned >= 1);

        var runs = (await admin.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?ruleId={rule}"))!;
        Assert.Equal([soon.Id], runs.Items.Select(r => r.RecordId));
    }

    [Fact]
    public async Task Inconsistent_maintenance_is_rejected_and_the_vehicle_cannot_be_changed_afterwards()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var vehicle = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM20" });
        var other = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM21" });

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Maintenance, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("vehicleId"));
        Assert.True(missing.Errors.ContainsKey("performedOn"));

        var inconsistent = await ValidationProblemAsync(await admin.PostAsJsonAsync(Maintenance, new RecordWriteRequest(new JsonObject
        {
            ["vehicleId"] = vehicle.Id.ToString(), ["performedOn"] = Iso(Today), ["odometerKm"] = 100_000,
            ["nextDueDate"] = Iso(Today.AddDays(-1)), ["nextDueKm"] = 90_000, ["cost"] = 100m,
        })));
        Assert.Equal(["Sonraki bakım tarihi bakım tarihinden sonra olmalı."], inconsistent.Errors["nextDueDate"]);
        Assert.Equal(["Sonraki bakım kilometresi bakım kilometresinden büyük olmalı."], inconsistent.Errors["nextDueKm"]);
        Assert.Equal(["Tutar girildiyse para birimi zorunludur."], inconsistent.Errors["currency"]);

        var future = await ValidationProblemAsync(await admin.PostAsJsonAsync(Maintenance, new RecordWriteRequest(new JsonObject
        {
            ["vehicleId"] = vehicle.Id.ToString(), ["performedOn"] = Iso(Today.AddDays(10)),
        })));
        Assert.True(future.Errors.ContainsKey("performedOn"));

        var record = await MaintainAsync(admin, vehicle.Id, Today.AddDays(90));
        var moved = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Maintenance}/{record.Id}",
            new RecordWriteRequest(new JsonObject { ["vehicleId"] = other.Id.ToString() }, record.Version)));
        Assert.True(moved.Errors.ContainsKey("vehicleId"));
        Assert.Null((await GetAsync(admin, $"{Vehicles}/{other.Id}")).Values["nextMaintenanceDate"]);
    }

    [Fact]
    public async Task Another_tenants_vehicles_and_maintenance_records_stay_invisible()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var vehicleA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "34BKM30" });
        var recordA = await MaintainAsync(adminA, vehicleA.Id, Today.AddDays(30));

        var foreign = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Maintenance, new RecordWriteRequest(new JsonObject
        {
            ["vehicleId"] = vehicleA.Id.ToString(), ["performedOn"] = Iso(Today),
        })));
        Assert.Equal(["Araç bulunamadı."], foreign.Errors["vehicleId"]);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Maintenance}/{recordA.Id}")).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<RecordPageResponse>(Maintenance))!.Items);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminB.DeleteAsync($"{Maintenance}/{recordA.Id}?version={recordA.Version}")).StatusCode);
        Assert.NotNull((await GetAsync(adminA, $"{Vehicles}/{vehicleA.Id}")).Values["nextMaintenanceDate"]);
    }

    [Fact]
    public async Task Readers_cannot_write_and_vehicle_access_does_not_grant_maintenance()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var vehiclesOnly = await LoginAsync(s.VehiclesOnly);
        var vehicle = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM40" });
        var record = await MaintainAsync(admin, vehicle.Id, Today.AddDays(45));

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Maintenance}/{record.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Maintenance, new RecordWriteRequest(new JsonObject
        {
            ["vehicleId"] = vehicle.Id.ToString(), ["performedOn"] = Iso(Today),
        }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Maintenance}/{record.Id}?version={record.Version}")).StatusCode);

        // Araç izni bakım kayıtlarını açmaz; aracın türetilmiş "sonraki bakım" alanı ise araç kaydının parçasıdır.
        Assert.Equal(HttpStatusCode.Forbidden, (await vehiclesOnly.GetAsync(Maintenance)).StatusCode);
        Assert.NotNull((await GetAsync(vehiclesOnly, $"{Vehicles}/{vehicle.Id}")).Values["nextMaintenanceDate"]);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Reader, ModulixUser VehiclesOnly);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        var reader = await fixture.Factory.CreateUserAsync(tenant);
        var vehiclesOnly = await fixture.Factory.CreateUserAsync(tenant);
        using (var scope = fixture.Factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await fixture.Factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await fixture.Factory.CreateRoleAsync(tenant.Id, [VehiclesRead, MaintenanceRead], Scope.All, reader);
        await fixture.Factory.CreateRoleAsync(tenant.Id, [VehiclesRead], Scope.All, vehiclesOnly);
        return new Setup(tenant.Id, admin, reader, vehiclesOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private Task<RecordResponse> MaintainAsync(HttpClient client, Guid vehicleId, DateOnly nextDue) =>
        PostAsync(client, Maintenance, new JsonObject
        {
            ["vehicleId"] = vehicleId.ToString(), ["kindKey"] = "Periodic", ["performedOn"] = Iso(Today.AddDays(-1)), ["nextDueDate"] = Iso(nextDue),
        });

    private static async Task<Guid> PublishRuleAsync(HttpClient client, AutomationDefinition definition)
    {
        var create = await client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            "fleet", "fleet.vehicle", $"Bakım {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        return created.Rule.Id;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

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
