using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 8: sefer masrafı. Mutlu yol (yakıt masrafı litre ve kilometreyle, bekleyen doğar, yönetici onaylar, reddedilen düzeltilip yeniden
/// bekletilir ve onaylanır), doğrulama (tutar ve para birimi, KDV tutarı aşamaz, ileri tarih, yakıtta litre zorunlu, yakıt dışı litre yok, rücu yalnızca
/// ceza, onaylı masraf değişmez, iptal edilen sefere masraf eklenmez, silinmez, ret gerekçesi zorunlu, durum genel uçtan yazılamaz, bağlı onay süreci
/// onaysız onayı engeller), tenant izolasyonu (başka tenant'ın masrafı ve seferi görünmez) ve yetki (onay izni olmayan onaylayamaz, okuma izni olmayan
/// göremez, izinsiz yazamaz).
/// </summary>
[Collection(EventTestCollection.Name)]
public class LogisticsExpenseTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Trips = "/api/records/logistics.trip";
    private const string Expenses = "/api/records/logistics.trip-expense";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_fuel_expense_is_pending_then_approved_and_a_rejected_one_is_corrected_resubmitted_and_approved()
    {
        var s = await SetUpAsync();
        using var clerk = await LoginAsync(s.Clerk);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(clerk, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        var fuel = await PostAsync(clerk, Expenses, Fuel(trip.Id, liters: 320.5m));
        Assert.Equal("pending", fuel.Values["status"]!.GetValue<string>());
        Assert.Equal("TRY", fuel.Values["currency"]!.GetValue<string>());
        Assert.Equal(320.5m, fuel.Values["liters"]!.GetValue<decimal>());
        Assert.Equal(412_345, fuel.Values["odometerKm"]!.GetValue<int>());

        var approved = await ActAsync(manager, fuel, "approve");
        Assert.Equal("approved", approved.Values["status"]!.GetValue<string>());
        Assert.Single((await manager.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?tripId={trip.Id}&status=approved"))!.Items);

        // Reddedilen masraf gerekçeyle kapanır, düzeltilir, yeniden bekletilir ve onaylanır.
        var toll = await PostAsync(clerk, Expenses, Plain(trip.Id, "Toll", 150m));
        var rejected = await ActAsync(manager, toll, "reject", "Fiş yok");
        Assert.Equal("rejected", rejected.Values["status"]!.GetValue<string>());
        Assert.Equal("Fiş yok", rejected.Values["statusReason"]!.GetValue<string>());

        var corrected = await PutAsync(clerk, $"{Expenses}/{toll.Id}", rejected.Version, new JsonObject { ["documentNo"] = "FS-2026-77", ["amount"] = 175 });
        Assert.Equal("rejected", corrected.Values["status"]!.GetValue<string>());
        var resubmitted = await ActAsync(clerk, corrected, "resubmit");
        Assert.Equal("pending", resubmitted.Values["status"]!.GetValue<string>());
        Assert.Null(resubmitted.Values["statusReason"]);
        Assert.Equal("approved", (await ActAsync(manager, resubmitted, "approve")).Values["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_expenses_and_closed_ones_are_rejected()
    {
        var s = await SetUpAsync();
        using var clerk = await LoginAsync(s.Clerk);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(clerk, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        async Task<ValidationProblemDetails> RejectedAsync(JsonObject values) =>
            await ValidationProblemAsync(await clerk.PostAsJsonAsync(Expenses, new RecordWriteRequest(values)));

        Assert.Contains("amount", (await RejectedAsync(Plain(trip.Id, "Toll", 0m))).Errors.Keys);
        Assert.Contains("currency", (await RejectedAsync(Plain(trip.Id, "Toll", 10m, currency: "TR"))).Errors.Keys);
        Assert.Contains("vatAmount", (await RejectedAsync(With(Plain(trip.Id, "Toll", 10m), ("vatAmount", 11)))).Errors.Keys);
        Assert.Contains("expenseDate", (await RejectedAsync(With(Plain(trip.Id, "Toll", 10m), ("expenseDate", Iso(Today.AddDays(5)))))).Errors.Keys);
        Assert.Contains("expenseTypeKey", (await RejectedAsync(With(Plain(trip.Id, "Toll", 10m), ("expenseTypeKey", "Yok")))).Errors.Keys);
        Assert.Contains("liters", (await RejectedAsync(Fuel(trip.Id, liters: null))).Errors.Keys); // yakıtta litre zorunlu
        Assert.Contains("liters", (await RejectedAsync(With(Plain(trip.Id, "Toll", 10m), ("liters", 5)))).Errors.Keys); // yakıt dışında litre yok
        Assert.Contains("recoverFromDriver", (await RejectedAsync(With(Plain(trip.Id, "Toll", 10m), ("recoverFromDriver", true)))).Errors.Keys);
        Assert.Empty((await clerk.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?tripId={trip.Id}"))!.Items); // hatalı denemeler hiçbir şey yazmadı

        // Ceza kategorisinde rücu bayrağı bilgi olarak girilir.
        var fine = await PostAsync(clerk, Expenses, With(Plain(trip.Id, "Fine", 1500m), ("recoverFromDriver", true)));
        Assert.True(fine.Values["recoverFromDriver"]!.GetValue<bool>());

        // Ret gerekçesiz olmaz; durum genel uçtan yazılamaz; onaylı masraf değişmez ve masraf silinmez.
        var reasonless = await manager.PostAsJsonAsync($"/api/logistics/expenses/{fine.Id}/actions", new RunLogisticsActionRequest("reject", fine.Version, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reasonless.StatusCode);
        var viaGeneric = await PutAsync(clerk, $"{Expenses}/{fine.Id}", fine.Version, new JsonObject { ["status"] = "approved" });
        Assert.Equal("pending", viaGeneric.Values["status"]!.GetValue<string>());
        var approved = await ActAsync(manager, viaGeneric, "approve");
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.PutAsJsonAsync($"{Expenses}/{fine.Id}", new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, approved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/expenses/{fine.Id}/actions", new RunLogisticsActionRequest("approve", approved.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.DeleteAsync($"{Expenses}/{fine.Id}?version={approved.Version}")).StatusCode);

        // İptal edilen sefere masraf eklenmez.
        var cancelled = await ActAsync(clerk, trip, "cancel", "Vazgeçildi", kind: "trips");
        Assert.Equal("cancelled", cancelled.Values["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.PostAsJsonAsync(Expenses, new RecordWriteRequest(Plain(trip.Id, "Toll", 10m)))).StatusCode);
    }

    [Fact]
    public async Task An_approval_process_bound_to_expense_approval_blocks_it_until_the_request_is_approved()
    {
        var s = await SetUpAsync();
        using var clerk = await LoginAsync(s.Clerk);
        using var manager = await LoginAsync(s.Manager);
        using var admin = await LoginAsync(s.Admin);
        var trip = await PostAsync(clerk, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(clerk, Expenses, Plain(trip.Id, "Toll", 900m));

        var process = await (await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Masraf onayı {Guid.NewGuid():N}"[..24], null, "TRY", [new ApprovalStepDefinitionDto("Müdür", 1, s.Approver.Id, null, null, null)],
            null, Modulix.Modules.Logistics.LogisticsModule.Entities.TripExpense, [Modulix.Modules.Logistics.LogisticsModule.ApprovalCommands.ApproveExpense])))
            .Content.ReadFromJsonAsync<ApprovalDefinitionDto>();

        // Süreç bağlıyken masraf onay talebi olmadan onaylanamaz: kapı nedeni 409 ile iletilir.
        var blocked = await manager.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions", new RunLogisticsActionRequest("approve", expense.Version, null));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("onay", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var approver = await LoginAsync(s.Approver);
        var opened = await clerk.PostAsJsonAsync("/api/approvals/requests", new OpenApprovalRequest(process!.Id, null, null, null, null, expense.Id));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var request = (await opened.Content.ReadFromJsonAsync<ApprovalRequestDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/approvals/requests/{request.Id}/approve", new DecideApprovalRequest(null, request.Version))).StatusCode);

        Assert.Equal("approved", (await ActAsync(manager, expense, "approve")).Values["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_tenants_expenses_and_trips_cannot_be_seen_or_used()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var clerkA = await LoginAsync(a.Clerk);
        using var managerB = await LoginAsync(b.Manager);
        using var clerkB = await LoginAsync(b.Clerk);
        var trip = await PostAsync(clerkA, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(clerkA, Expenses, Plain(trip.Id, "Toll", 100m));

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Expenses}/{expense.Id}")).StatusCode);
        Assert.Empty((await managerB.GetFromJsonAsync<RecordPageResponse>(Expenses))!.Items);
        Assert.Empty((await managerB.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?tripId={trip.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions", new RunLogisticsActionRequest("approve", expense.Version, null))).StatusCode);

        // Başka tenant'ın seferine masraf eklenemez (sefer "bulunamadı" sayılır).
        var onForeignTrip = await clerkB.PostAsJsonAsync(Expenses, new RecordWriteRequest(Plain(trip.Id, "Toll", 100m)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onForeignTrip.StatusCode);
        Assert.Contains("tripId", (await onForeignTrip.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
    }

    [Fact]
    public async Task Only_those_with_the_approve_permission_decide_and_unauthorized_users_cannot_read_or_write()
    {
        var s = await SetUpAsync();
        using var clerk = await LoginAsync(s.Clerk);
        using var viewer = await LoginAsync(s.Viewer);
        using var outsider = await LoginAsync(s.Outsider);
        var trip = await PostAsync(clerk, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(clerk, Expenses, Plain(trip.Id, "Toll", 100m));

        // Girişi yapan onay iznine sahip değildir: onaylayamaz ve reddedemez; eylem listesinde onay yoktur.
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions", new RunLogisticsActionRequest("approve", expense.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions", new RunLogisticsActionRequest("reject", expense.Version, "x"))).StatusCode);
        var actions = (await clerk.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/expenses/{expense.Id}/actions"))!;
        Assert.DoesNotContain(actions, a => a.Action == "approve");

        // Yalnızca okuyan görür ama yazamaz.
        Assert.Equal(expense.Id, Assert.Single((await viewer.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?tripId={trip.Id}"))!.Items).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Expenses, new RecordWriteRequest(Plain(trip.Id, "Toll", 5m)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"{Expenses}/{expense.Id}", new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, expense.Version))).StatusCode);

        // İzinsiz kişi masrafı hiç göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Expenses}/{expense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Expenses)).StatusCode);
    }

    // --- yardımcılar ---

    private static JsonObject Plain(Guid tripId, string type, decimal amount, string currency = "TRY") => new()
    {
        ["tripId"] = tripId.ToString(), ["expenseTypeKey"] = type, ["expenseDate"] = Iso(Today), ["amount"] = amount, ["currency"] = currency,
    };

    private static JsonObject Fuel(Guid tripId, decimal? liters)
    {
        var values = Plain(tripId, "Diesel", 12_800.75m);
        values["odometerKm"] = 412_345;
        if (liters is { } l)
        {
            values["liters"] = l;
        }

        return values;
    }

    private static JsonObject With(JsonObject values, (string Key, JsonNode? Value) change)
    {
        values[change.Key] = change.Value;
        return values;
    }

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Clerk, ModulixUser Manager, ModulixUser Approver, ModulixUser Viewer, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var clerk = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);

        string[] trips = [TripsRead, TripsWrite];
        await factory.CreateRoleAsync(tenant.Id, [.. trips, ExpensesRead, ExpensesWrite, CoreModule.Permissions.RequestApprovals], Scope.All, clerk);
        await factory.CreateRoleAsync(tenant.Id, [.. trips, ExpensesRead, ExpensesWrite, ExpensesApprove], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.DecideApprovals], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead, ExpensesRead], Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead], Scope.All, outsider);
        return new Setup(tenant.Id, admin, clerk, manager, approver, viewer, outsider);
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, RecordResponse record, string action, string? reason = null, string kind = "expenses")
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, long version, JsonObject values)
    {
        var response = await client.PutAsJsonAsync(url, new RecordWriteRequest(values, version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

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
