using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 dilim 4: zimmet takibi. Mutlu yol (demirbaş, zimmetleme, çalışanın teslim onayı, tutanak PDF'i, iade ve yeniden zimmet, denetim kaydı),
/// doğrulama (envanter no tekilliği, elle "zimmetli" yapılamaz, depoda olmayan demirbaş zimmetlenmez, ayrılmış çalışan, tarihler, zimmet değişmez ve
/// silinmez, geçmişi olan demirbaş silinmez, kayıp açıklaması), tenant izolasyonu ve yetki (çalışan yalnızca kendi zimmetini görür ve onaylar,
/// demirbaş ekleyemez ve iade alamaz; izinsiz 403).
/// </summary>
public class HrAssetTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Assets = "/api/records/hr.asset";
    private const string Assignments = "/api/records/hr.asset-assignment";
    private const string Actions = "/api/hr/asset-assignments";

    [Fact]
    public async Task Asset_is_assigned_acknowledged_documented_returned_and_reassigned()
    {
        var s = await SetUpAsync();
        using var keeper = await LoginAsync(s.Keeper);
        using var staff = await LoginAsync(s.Staff);

        var laptop = await PostAsync(keeper, Assets, new JsonObject
        {
            ["assetTag"] = "lt-001", ["name"] = "Dizüstü bilgisayar", ["categoryKey"] = "Laptop", ["serialNumber"] = "SN123", ["brand"] = "Marka",
            ["purchaseCost"] = 42000.5m, ["currency"] = "try",
        });
        Assert.Equal(("LT-001", "inStock", "TRY"), (Text(laptop, "assetTag"), Text(laptop, "status"), Text(laptop, "currency")));

        var assignment = await PostAsync(keeper, Assignments, new JsonObject
        {
            ["assetId"] = laptop.Id.ToString(), ["employeeId"] = s.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02", ["conditionOut"] = "Şarj aleti ile",
        });
        Assert.Equal(("active", s.Staff.Id.ToString(), "LT-001"), (Text(assignment, "status"), Text(assignment, "employeeUserId"), Text(assignment, "assetTag")));
        var assigned = await GetAsync(keeper, $"{Assets}/{laptop.Id}");
        Assert.Equal(("assigned", "Sinan Çalışır"), (Text(assigned, "status"), Text(assigned, "currentEmployeeName")));

        // Çalışan üzerindeki demirbaşı ve zimmeti görür, teslim aldığını onaylar ve tutanağı indirir.
        Assert.Equal([laptop.Id], (await ListAsync(staff, Assets, null)).Select(a => a.Id));
        var actions = (await staff.GetFromJsonAsync<List<HrActionDto>>($"{Actions}/{assignment.Id}/actions"))!;
        Assert.Equal(["acknowledge"], actions.Select(a => a.Action));
        var acknowledged = await ActAsync(staff, assignment.Id, "acknowledge", null);
        Assert.NotNull(acknowledged.Values["acknowledgedAt"]);
        var pdf = await staff.GetAsync($"{Actions}/{assignment.Id}/document");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF"u8.ToArray(), (await pdf.Content.ReadAsByteArrayAsync())[..4]);

        // İade: zimmet kapanır, demirbaş depoya döner; çalışan artık demirbaşı görmez ama geçmiş zimmetini görür.
        var returned = await ActAsync(keeper, assignment.Id, "return", "Çiziksiz");
        Assert.Equal(("returned", "Çiziksiz"), (Text(returned, "status"), Text(returned, "conditionIn")));
        var back = await GetAsync(keeper, $"{Assets}/{laptop.Id}");
        Assert.Equal("inStock", Text(back, "status"));
        Assert.Null(back.Values["currentEmployeeId"]);
        Assert.Empty(await ListAsync(staff, Assets, null));
        Assert.Equal([assignment.Id], (await ListAsync(staff, Assignments, "status=returned")).Select(a => a.Id));

        // Yeniden zimmetlenir; geçmiş demirbaş kartında kalır.
        await PostAsync(keeper, Assignments, new JsonObject
        {
            ["assetId"] = laptop.Id.ToString(), ["employeeId"] = s.OtherEmployee.ToString(), ["assignedOn"] = "2026-11-10",
        });
        Assert.Equal(2, (await ListAsync(keeper, Assignments, $"assetId={laptop.Id}")).Count);
        var history = (await keeper.GetFromJsonAsync<AuditPageResponse>($"{Assignments}/{assignment.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));
    }

    [Fact]
    public async Task Invalid_assets_and_assignments_are_rejected_and_records_are_kept()
    {
        var s = await SetUpAsync();
        using var keeper = await LoginAsync(s.Keeper);
        using var staff = await LoginAsync(s.Staff);

        var phone = await PostAsync(keeper, Assets, new JsonObject { ["assetTag"] = "TEL-1", ["name"] = "Telefon", ["categoryKey"] = "Phone" });
        var duplicate = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assets,
            new RecordWriteRequest(new JsonObject { ["assetTag"] = "tel-1", ["name"] = "Başka", ["categoryKey"] = "Phone" })));
        Assert.True(duplicate.Errors.ContainsKey("assetTag"));
        var manualAssigned = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assets,
            new RecordWriteRequest(new JsonObject { ["assetTag"] = "X-1", ["name"] = "X", ["categoryKey"] = "Phone", ["status"] = "assigned" })));
        Assert.True(manualAssigned.Errors.ContainsKey("status"));
        var costWithoutCurrency = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assets,
            new RecordWriteRequest(new JsonObject { ["assetTag"] = "X-2", ["name"] = "X", ["categoryKey"] = "Phone", ["purchaseCost"] = 10m })));
        Assert.True(costWithoutCurrency.Errors.ContainsKey("currency"));

        // Bakımdaki demirbaş, ayrılmış çalışan ve tarih kuralları.
        var broken = await PostAsync(keeper, Assets, new JsonObject { ["assetTag"] = "MON-1", ["name"] = "Monitör", ["categoryKey"] = "Monitor", ["status"] = "maintenance" });
        var inMaintenance = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assignments, Assign(broken.Id, s.StaffEmployee, "2026-11-02")));
        Assert.True(inMaintenance.Errors.ContainsKey("assetId"));
        var left = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assignments, Assign(phone.Id, s.LeftEmployee, "2026-11-02")));
        Assert.True(left.Errors.ContainsKey("employeeId"));
        var future = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assignments, Assign(phone.Id, s.StaffEmployee, "2099-01-01")));
        Assert.True(future.Errors.ContainsKey("assignedOn"));
        var expectedBefore = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assignments,
            new RecordWriteRequest(new JsonObject
            {
                ["assetId"] = phone.Id.ToString(), ["employeeId"] = s.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02", ["expectedReturnOn"] = "2026-11-01",
            })));
        Assert.True(expectedBefore.Errors.ContainsKey("expectedReturnOn"));

        var assignment = await PostAsync(keeper, Assignments, new JsonObject
        {
            ["assetId"] = phone.Id.ToString(), ["employeeId"] = s.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02",
        });
        var again = await ValidationProblemAsync(await keeper.PostAsJsonAsync(Assignments, Assign(phone.Id, s.OtherEmployee, "2026-11-03")));
        Assert.Contains("zimmetli", again.Errors["assetId"].Single());

        // Zimmetteki demirbaşın durumu elle değişmez; zimmet değişmez ve silinmez; geçmişi olan demirbaş silinmez.
        var assignedPhone = await GetAsync(keeper, $"{Assets}/{phone.Id}");
        var statusChange = await ValidationProblemAsync(await keeper.PutAsJsonAsync($"{Assets}/{phone.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "maintenance" }, assignedPhone.Version)));
        Assert.True(statusChange.Errors.ContainsKey("status"));
        Assert.Equal(HttpStatusCode.Conflict, (await keeper.PutAsJsonAsync($"{Assignments}/{assignment.Id}",
            new RecordWriteRequest(new JsonObject { ["conditionOut"] = "değişti" }, assignment.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await keeper.DeleteAsync($"{Assignments}/{assignment.Id}?version={assignment.Version}")).StatusCode);

        // Teslim onayını yalnızca çalışan verir, bir kez; kayıp açıklama ister; kapanmış zimmet yeniden kapanmaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(keeper, assignment, "acknowledge", null)).StatusCode);
        var acknowledged = await ActAsync(staff, assignment.Id, "acknowledge", null);
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(staff, acknowledged, "acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(keeper, acknowledged, "lost", " ")).StatusCode);
        var lost = await ActAsync(keeper, assignment.Id, "lost", "Taksi'de unutuldu");
        Assert.Equal("lost", Text(lost, "status"));
        Assert.Equal("lost", Text(await GetAsync(keeper, $"{Assets}/{phone.Id}"), "status"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(keeper, lost, "return", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(keeper, lost, "archive", null)).StatusCode);

        var phoneNow = await GetAsync(keeper, $"{Assets}/{phone.Id}");
        Assert.Equal(HttpStatusCode.Conflict, (await keeper.DeleteAsync($"{Assets}/{phone.Id}?version={phoneNow.Version}")).StatusCode);
        var spare = await PostAsync(keeper, Assets, new JsonObject { ["assetTag"] = "YDK-1", ["name"] = "Yedek", ["categoryKey"] = "Tool" });
        Assert.Equal(HttpStatusCode.NoContent, (await keeper.DeleteAsync($"{Assets}/{spare.Id}?version={spare.Version}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_assets_and_assignments_are_out_of_reach()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var keeperA = await LoginAsync(a.Keeper);
        using var keeperB = await LoginAsync(b.Keeper);
        var assetA = await PostAsync(keeperA, Assets, new JsonObject { ["assetTag"] = "ORT-1", ["name"] = "Ortak ad", ["categoryKey"] = "Laptop" });
        var assignmentA = await PostAsync(keeperA, Assignments, new JsonObject
        {
            ["assetId"] = assetA.Id.ToString(), ["employeeId"] = a.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02",
        });

        var foreignAsset = await ValidationProblemAsync(await keeperB.PostAsJsonAsync(Assignments, Assign(assetA.Id, b.StaffEmployee, "2026-11-02")));
        Assert.Equal(["Demirbaş bulunamadı."], foreignAsset.Errors["assetId"]);
        var foreignEmployee = await ValidationProblemAsync(await keeperB.PostAsJsonAsync(Assignments, Assign(assetA.Id, a.StaffEmployee, "2026-11-02")));
        Assert.Equal(["Çalışan bulunamadı."], foreignEmployee.Errors["employeeId"]);

        Assert.DoesNotContain(await ListAsync(keeperB, Assets, null), x => x.Id == assetA.Id);
        Assert.DoesNotContain(await ListAsync(keeperB, Assignments, null), x => x.Id == assignmentA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await keeperB.GetAsync($"{Assets}/{assetA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keeperB.GetAsync($"{Actions}/{assignmentA.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await keeperB.GetAsync($"{Actions}/{assignmentA.Id}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(keeperB, assignmentA, "return", null)).StatusCode);

        // Aynı envanter numarası başka tenant'ta serbesttir.
        await PostAsync(keeperB, Assets, new JsonObject { ["assetTag"] = "ORT-1", ["name"] = "Ortak ad", ["categoryKey"] = "Laptop" });
    }

    [Fact]
    public async Task Staff_see_and_acknowledge_only_their_own_assignments_and_cannot_manage_assets()
    {
        var s = await SetUpAsync();
        using var keeper = await LoginAsync(s.Keeper);
        using var staff = await LoginAsync(s.Staff);
        using var outsider = await LoginAsync(s.Outsider);

        var stock = await PostAsync(keeper, Assets, new JsonObject { ["assetTag"] = "STK-1", ["name"] = "Depodaki", ["categoryKey"] = "Laptop" });
        var others = await PostAsync(keeper, Assets, new JsonObject { ["assetTag"] = "OTH-1", ["name"] = "Başkasının", ["categoryKey"] = "Phone" });
        var othersAssignment = await PostAsync(keeper, Assignments, new JsonObject
        {
            ["assetId"] = others.Id.ToString(), ["employeeId"] = s.OtherEmployee.ToString(), ["assignedOn"] = "2026-11-02",
        });

        // "Kendi" kapsamı: depodaki ve başkasındaki demirbaş ile başkasının zimmeti görünmez; tutanağı indirilemez; onaylanamaz.
        Assert.Empty(await ListAsync(staff, Assets, null));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Assets}/{stock.Id}")).StatusCode);
        Assert.Empty(await ListAsync(staff, Assignments, null));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Actions}/{othersAssignment.Id}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(staff, othersAssignment, "acknowledge", null)).StatusCode);

        // Çalışan demirbaş ekleyemez, zimmetleyemez, iade alamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Assets,
            new RecordWriteRequest(new JsonObject { ["assetTag"] = "BEN-1", ["name"] = "X", ["categoryKey"] = "Laptop" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Assignments, Assign(stock.Id, s.StaffEmployee, "2026-11-02"))).StatusCode);
        var mine = await PostAsync(keeper, Assignments, new JsonObject
        {
            ["assetId"] = stock.Id.ToString(), ["employeeId"] = s.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02",
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, mine, "return", null)).StatusCode);
        Assert.DoesNotContain((await staff.GetFromJsonAsync<List<HrActionDto>>($"{Actions}/{mine.Id}/actions"))!, a => a.Action == "return");

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Assets)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Assignments)).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Assets)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(
        Guid TenantId, ModulixUser Keeper, ModulixUser Staff, ModulixUser Outsider, Guid StaffEmployee, Guid OtherEmployee, Guid LeftEmployee);

    /// <summary>Demirbaş sorumlusu (tüm kapsam), çalışan ("kendi" kapsam, kartına bağlı), izinsiz kullanıcı; ayrıca başka bir çalışan ve ayrılmış çalışan.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var keeper = await factory.CreateUserAsync(tenant);
        var staff = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, AssetsRead, AssetsManage], Scope.All, keeper);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, AssetsRead], Scope.Own, staff);

        using var hr = await LoginAsync(admin);
        var staffCard = await EmployeeAsync(hr, "Sinan", "Çalışır", staff.Id, terminated: false);
        var otherCard = await EmployeeAsync(hr, "Deniz", "Başka", userId: null, terminated: false);
        var leftCard = await EmployeeAsync(hr, "Ece", "Ayrıldı", userId: null, terminated: true);
        return new Setup(tenant.Id, keeper, staff, outsider, staffCard, otherCard, leftCard);
    }

    private static async Task<Guid> EmployeeAsync(HttpClient client, string first, string last, Guid? userId, bool terminated)
    {
        var person = await PostAsync(client, People, new JsonObject { ["firstName"] = first, ["lastName"] = last });
        return (await PostAsync(client, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = userId?.ToString(),
            ["status"] = terminated ? "terminated" : "active", ["terminationDate"] = terminated ? "2025-12-31" : null,
        })).Id;
    }

    private static RecordWriteRequest Assign(Guid assetId, Guid employeeId, string on) =>
        new(new JsonObject { ["assetId"] = assetId.ToString(), ["employeeId"] = employeeId.ToString(), ["assignedOn"] = on });

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

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

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, RecordResponse assignment, string action, string? reason) =>
        client.PostAsJsonAsync($"{Actions}/{assignment.Id}/actions", new RunHrActionRequest(action, assignment.Version, reason));

    private static async Task<RecordResponse> ActAsync(HttpClient client, Guid assignmentId, string action, string? reason)
    {
        var current = await GetAsync(client, $"{Assignments}/{assignmentId}");
        var response = await PostActionAsync(client, current, action, reason);
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
