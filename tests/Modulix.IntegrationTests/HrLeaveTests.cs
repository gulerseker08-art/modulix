using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 dilim 2: izin yönetimi. Mutlu yol (tatil ve hak tanımı, çalışanın kendi talebi, gün hesabı, yöneticinin onayı, bakiye düşümü, İK
/// iptaliyle bakiyenin geri gelmesi, önizleme), doğrulama (çakışma, yetersiz ve tanımsız bakiye, yarım gün, yıl aşımı, hafta sonu, talep
/// değiştirilemez ve silinemez, ret gerekçesi, bekleyen dışı onay), tenant izolasyonu (başka tenant'ın çalışanı, talebi ve eylemleri) ve yetki
/// (çalışan başkası adına talep açamaz, onaylayamaz, başkasının talebini ve hakkını göremez; yönetici kendi talebini onaylayamaz; izinsiz 403).
/// 2027-03-01 pazartesidir.
/// </summary>
public class HrLeaveTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Requests = "/api/records/hr.leave-request";
    private const string Balances = "/api/records/hr.leave-balance";
    private const string Holidays = "/api/records/hr.holiday";

    [Fact]
    public async Task Employee_requests_annual_leave_manager_approves_and_balance_follows_the_decisions()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);

        await PostAsync(hr, Holidays, new JsonObject { ["date"] = "2027-03-03", ["name"] = "İdari izin" });
        var balance = await PostAsync(hr, Balances, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["year"] = 2027, ["entitledDays"] = 14, ["carriedOverDays"] = 2 });
        Assert.Equal(16m, balance.Values["remainingDays"]!.GetValue<decimal>());

        var preview = await staff.PostAsJsonAsync("/api/hr/leave-requests/preview",
            new LeavePreviewRequest(s.StaffEmployee, "Annual", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 5), false));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var days = (await preview.Content.ReadFromJsonAsync<LeavePreviewDto>())!;
        Assert.Equal((4m, 16m, 0m), (days.Days, days.RemainingDays, days.PendingDays));

        // Çalışan kendi adına açar: hafta içi 5 gün, 1 gün tatil → 4 gün; onaylayan talep anındaki yöneticidir.
        var request = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = s.StaffEmployee.ToString(), ["leaveTypeKey"] = "Annual", ["startDate"] = "2027-03-01", ["endDate"] = "2027-03-05",
        });
        Assert.Equal(4m, request.Values["days"]!.GetValue<decimal>());
        Assert.Equal("pending", request.Values["status"]!.GetValue<string>());
        Assert.Equal(s.Manager.Id.ToString(), request.Values["approverUserId"]!.GetValue<string>());
        Assert.Equal("annual", request.Values["leaveCategory"]!.GetValue<string>());

        // Yönetici "kendi" kapsamıyla da astının talebini görür ve onaylar; eylemler sunucudan gelir.
        Assert.Contains((await ListAsync(manager, Requests, "status=pending")), r => r.Id == request.Id);
        var actions = (await manager.GetFromJsonAsync<List<HrActionDto>>($"/api/hr/leave-requests/{request.Id}/actions"))!;
        Assert.True(actions.Single(a => a.Action == "approve").Allowed);
        var approved = await ActAsync(manager, request, "approve", null);
        Assert.Equal("approved", approved.Values["status"]!.GetValue<string>());
        Assert.Equal(s.Manager.Id.ToString(), approved.Values["decidedByUserId"]!.GetValue<string>());
        Assert.Equal(12m, (await GetAsync(hr, $"{Balances}/{balance.Id}")).Values["remainingDays"]!.GetValue<decimal>());

        // Yıllık olmayan izin haktan düşmez.
        var excuse = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = s.StaffEmployee.ToString(), ["leaveTypeKey"] = "Excuse", ["startDate"] = "2027-03-10", ["endDate"] = "2027-03-10", ["halfDay"] = true,
        });
        Assert.Equal(0.5m, excuse.Values["days"]!.GetValue<decimal>());
        await ActAsync(manager, excuse, "approve", null);
        Assert.Equal(12m, (await GetAsync(hr, $"{Balances}/{balance.Id}")).Values["remainingDays"]!.GetValue<decimal>());

        // Onaylanmış izni çalışan iptal edemez; İK iptal eder ve gün bakiyeye geri döner.
        var staffActions = (await staff.GetFromJsonAsync<List<HrActionDto>>($"/api/hr/leave-requests/{request.Id}/actions"))!;
        Assert.False(staffActions.Single(a => a.Action == "cancel").Allowed);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/hr/leave-requests/{request.Id}/actions",
            new RunHrActionRequest("cancel", approved.Version, null))).StatusCode);
        var cancelled = await ActAsync(hr, approved, "cancel", "Plan değişti");
        Assert.Equal("cancelled", cancelled.Values["status"]!.GetValue<string>());
        Assert.Equal(16m, (await GetAsync(hr, $"{Balances}/{balance.Id}")).Values["remainingDays"]!.GetValue<decimal>());

        // Çalışan bekleyen talebini kendisi iptal eder.
        var own = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = s.StaffEmployee.ToString(), ["leaveTypeKey"] = "Annual", ["startDate"] = "2027-04-05", ["endDate"] = "2027-04-06",
        });
        Assert.Equal("cancelled", (await ActAsync(staff, own, "cancel", null)).Values["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_and_requests_change_only_through_actions()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);
        var employee = s.StaffEmployee.ToString();

        var noBalance = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Annual", "2027-03-01", "2027-03-02")));
        Assert.Contains("izin hakkı tanımlanmamış", noBalance.Errors["leaveTypeKey"].Single());

        await PostAsync(hr, Balances, new JsonObject { ["employeeId"] = employee, ["year"] = 2027, ["entitledDays"] = 3 });
        var duplicateBalance = await ValidationProblemAsync(await hr.PostAsJsonAsync(Balances,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = employee, ["year"] = 2027, ["entitledDays"] = 5 })));
        Assert.True(duplicateBalance.Errors.ContainsKey("year"));

        var tooLong = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Annual", "2027-03-01", "2027-03-05")));
        Assert.StartsWith("Yetersiz izin bakiyesi", tooLong.Errors["leaveTypeKey"].Single());

        var first = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = employee, ["leaveTypeKey"] = "Annual", ["startDate"] = "2027-03-01", ["endDate"] = "2027-03-02",
        });

        // Bekleyen 2 gün + istenen 2 gün > 3 gün hak.
        var pendingCounts = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Annual", "2027-03-08", "2027-03-09")));
        Assert.Contains("onay bekleyen 2 gün", pendingCounts.Errors["leaveTypeKey"].Single());

        var overlap = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Excuse", "2027-03-02", "2027-03-03")));
        Assert.Contains("başka bir izin talebi", overlap.Errors["startDate"].Single());

        var weekend = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Excuse", "2027-03-06", "2027-03-07")));
        Assert.Contains("iş günü yok", weekend.Errors["startDate"].Single());

        var halfDays = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Excuse", "2027-03-10", "2027-03-11", halfDay: true)));
        Assert.True(halfDays.Errors.ContainsKey("halfDay"));

        var crossYear = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Excuse", "2027-12-30", "2028-01-03")));
        Assert.Contains("tek takvim yılı", crossYear.Errors["endDate"].Single());

        var reversed = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(employee, "Excuse", "2027-03-12", "2027-03-11")));
        Assert.True(reversed.Errors.ContainsKey("endDate"));

        // Talep genel uçtan değiştirilemez ve silinemez; durum alanı genel uçtan yazılmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PutAsJsonAsync($"{Requests}/{first.Id}",
            new RecordWriteRequest(new JsonObject { ["description"] = "değişti" }, first.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Requests}/{first.Id}?version={first.Version}")).StatusCode);

        // Ret gerekçe ister; bekleyen dışındaki talep onaylanamaz; bilinmeyen eylem 422.
        var noReason = await manager.PostAsJsonAsync($"/api/hr/leave-requests/{first.Id}/actions", new RunHrActionRequest("reject", first.Version, " "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var rejected = await ActAsync(manager, first, "reject", "Yoğun dönem");
        Assert.Equal("Yoğun dönem", rejected.Values["decisionReason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/hr/leave-requests/{first.Id}/actions",
            new RunHrActionRequest("approve", rejected.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"/api/hr/leave-requests/{first.Id}/actions",
            new RunHrActionRequest("archive", rejected.Version, null))).StatusCode);

        // Kullanılmış hak kullanılanın altına çekilemez; izin kullanılmış hak silinmez.
        var approvedRequest = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = employee, ["leaveTypeKey"] = "Annual", ["startDate"] = "2027-03-15", ["endDate"] = "2027-03-16",
        });
        await ActAsync(manager, approvedRequest, "approve", null);
        var balance = (await ListAsync(hr, Balances, $"employeeId={employee}&year=2027")).Single();
        var shrink = await ValidationProblemAsync(await hr.PutAsJsonAsync($"{Balances}/{balance.Id}",
            new RecordWriteRequest(new JsonObject { ["entitledDays"] = 1 }, balance.Version)));
        Assert.True(shrink.Errors.ContainsKey("entitledDays"));
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Balances}/{balance.Id}?version={balance.Version}")).StatusCode);

        var holiday = await PostAsync(hr, Holidays, new JsonObject { ["date"] = "2027-05-19", ["name"] = "Gençlik ve Spor Bayramı" });
        var sameDay = await ValidationProblemAsync(await hr.PostAsJsonAsync(Holidays,
            new RecordWriteRequest(new JsonObject { ["date"] = "2027-05-19", ["name"] = "Tekrar" })));
        Assert.True(sameDay.Errors.ContainsKey("date"));
        Assert.Equal(HttpStatusCode.NoContent, (await hr.DeleteAsync($"{Holidays}/{holiday.Id}?version={holiday.Version}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_employees_requests_and_actions_are_out_of_reach()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hrA = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var staffA = await LoginAsync(a.Staff);
        var requestA = await PostAsync(staffA, Requests, new JsonObject
        {
            ["employeeId"] = a.StaffEmployee.ToString(), ["leaveTypeKey"] = "Excuse", ["startDate"] = "2027-03-01", ["endDate"] = "2027-03-01",
        });

        var foreignEmployee = await ValidationProblemAsync(await hrB.PostAsJsonAsync(Requests, Write(a.StaffEmployee.ToString(), "Excuse", "2027-03-02", "2027-03-02")));
        Assert.Equal(["Çalışan bulunamadı."], foreignEmployee.Errors["employeeId"]);
        var foreignBalance = await ValidationProblemAsync(await hrB.PostAsJsonAsync(Balances,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = a.StaffEmployee.ToString(), ["year"] = 2027, ["entitledDays"] = 14 })));
        Assert.Equal(["Çalışan bulunamadı."], foreignBalance.Errors["employeeId"]);

        Assert.DoesNotContain(await ListAsync(hrB, Requests, null), r => r.Id == requestA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Requests}/{requestA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"/api/hr/leave-requests/{requestA.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.PostAsJsonAsync($"/api/hr/leave-requests/{requestA.Id}/actions",
            new RunHrActionRequest("approve", requestA.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.PostAsJsonAsync("/api/hr/leave-requests/preview",
            new LeavePreviewRequest(a.StaffEmployee, "Annual", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 2), false))).StatusCode);

        // Aynı tarihte tatil başka tenant'ta serbesttir.
        await PostAsync(hrA, Holidays, new JsonObject { ["date"] = "2027-10-29", ["name"] = "Cumhuriyet Bayramı" });
        await PostAsync(hrB, Holidays, new JsonObject { ["date"] = "2027-10-29", ["name"] = "Cumhuriyet Bayramı" });
    }

    [Fact]
    public async Task Staff_act_only_for_themselves_managers_never_approve_their_own_and_outsiders_are_denied()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);
        using var outsider = await LoginAsync(s.Outsider);

        // Çalışan başkası adına talep açamaz; hak ve tatil yazamaz.
        var forOther = await ValidationProblemAsync(await staff.PostAsJsonAsync(Requests, Write(s.ManagerEmployee.ToString(), "Excuse", "2027-03-01", "2027-03-01")));
        Assert.Equal(["Yalnızca kendiniz için izin talebi açabilirsiniz."], forOther.Errors["employeeId"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Balances,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["year"] = 2027, ["entitledDays"] = 30 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Holidays,
            new RecordWriteRequest(new JsonObject { ["date"] = "2027-03-02", ["name"] = "Benim tatilim" }))).StatusCode);

        // Yöneticinin kendi talebinin onaylayanı kendi yöneticisidir (yok: İK); kendi talebini onaylayamaz.
        var managersOwn = await PostAsync(manager, Requests, new JsonObject
        {
            ["employeeId"] = s.ManagerEmployee.ToString(), ["leaveTypeKey"] = "Excuse", ["startDate"] = "2027-03-01", ["endDate"] = "2027-03-01",
        });
        Assert.Null(managersOwn.Values["approverUserId"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/hr/leave-requests/{managersOwn.Id}/actions",
            new RunHrActionRequest("approve", managersOwn.Version, null))).StatusCode);

        // "Kendi" kapsamındaki çalışan yöneticinin talebini ve İK'nın açtığı başkasına ait hakları göremez; onaylayamaz.
        await PostAsync(hr, Balances, new JsonObject { ["employeeId"] = s.ManagerEmployee.ToString(), ["year"] = 2027, ["entitledDays"] = 20 });
        await PostAsync(hr, Balances, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["year"] = 2027, ["entitledDays"] = 14 });
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Requests}/{managersOwn.Id}")).StatusCode);
        Assert.DoesNotContain(await ListAsync(staff, Requests, null), r => r.Id == managersOwn.Id);
        Assert.Equal([s.StaffEmployee.ToString()], (await ListAsync(staff, Balances, null)).Select(b => b.Values["employeeId"]!.GetValue<string>()));

        var staffRequest = await PostAsync(staff, Requests, new JsonObject
        {
            ["employeeId"] = s.StaffEmployee.ToString(), ["leaveTypeKey"] = "Excuse", ["startDate"] = "2027-03-02", ["endDate"] = "2027-03-02",
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync($"/api/hr/leave-requests/{staffRequest.Id}/actions",
            new RunHrActionRequest("approve", staffRequest.Version, null))).StatusCode);
        Assert.DoesNotContain((await staff.GetFromJsonAsync<List<HrActionDto>>($"/api/hr/leave-requests/{staffRequest.Id}/actions"))!, a => a.Action == "approve");

        // İK yöneticinin talebini onaylar (onaylayanı olmayan talep).
        Assert.Equal("approved", (await ActAsync(hr, managersOwn, "approve", null)).Values["status"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Requests)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(Requests, Write(s.StaffEmployee.ToString(), "Excuse", "2027-03-03", "2027-03-03"))).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Requests)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(
        Guid TenantId, ModulixUser Hr, ModulixUser Manager, ModulixUser Staff, ModulixUser Outsider, Guid ManagerEmployee, Guid StaffEmployee);

    /// <summary>İK (yönetici rolü, tüm kapsam), yönetici ve çalışan ("kendi" kapsam, kartlarına bağlı) ve izinsiz kullanıcı; çalışanın yöneticisi yöneticidir.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var managerUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, LeavesRead, LeavesRequest, LeavesApprove], Scope.Own, managerUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, LeavesRead, LeavesRequest], Scope.Own, staffUser);

        using var hr = await LoginAsync(hrUser);
        var managerCard = await EmployeeAsync(hr, "Melek", "Yönetir", managerUser.Id, managerEmployeeId: null);
        var staffCard = await EmployeeAsync(hr, "Sinan", "Çalışır", staffUser.Id, managerCard);
        return new Setup(tenant.Id, hrUser, managerUser, staffUser, outsider, managerCard, staffCard);
    }

    private static async Task<Guid> EmployeeAsync(HttpClient client, string first, string last, Guid userId, Guid? managerEmployeeId)
    {
        var person = await PostAsync(client, People, new JsonObject { ["firstName"] = first, ["lastName"] = last });
        return (await PostAsync(client, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = userId.ToString(),
            ["managerEmployeeId"] = managerEmployeeId?.ToString(),
        })).Id;
    }

    private static RecordWriteRequest Write(string employeeId, string type, string start, string end, bool halfDay = false) =>
        new(new JsonObject { ["employeeId"] = employeeId, ["leaveTypeKey"] = type, ["startDate"] = start, ["endDate"] = end, ["halfDay"] = halfDay });

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

    private static async Task<RecordResponse> ActAsync(HttpClient client, RecordResponse request, string action, string? reason)
    {
        var current = (await client.GetFromJsonAsync<RecordResponse>($"{Requests}/{request.Id}"))!;
        var response = await client.PostAsJsonAsync($"/api/hr/leave-requests/{request.Id}/actions", new RunHrActionRequest(action, current.Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? url : $"{url}?{query}"))!.Items;

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
