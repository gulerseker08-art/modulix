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
/// ADR-0042 dilim 3: masraf yönetimi. Mutlu yol (çalışanın beyanı, satırlar ve toplam, onaya gönderme, iade ve düzeltme, yönetici onayı, finansın
/// ödeme işareti, denetim kaydı), doğrulama (satırsız gönderim, tutar/KDV/tarih/para birimi, taslak dışında değişiklik, silme yasağı, gerekçe),
/// tenant izolasyonu (başka tenant'ın beyanı, satırı ve eylemleri) ve yetki (çalışan başkası adına beyan açamaz ve başkasının beyanını göremez;
/// yönetici kendi beyanına karar veremez; finans karar veremez, öder; izinsiz 403).
/// </summary>
public class HrExpenseTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Claims = "/api/records/hr.expense-claim";
    private const string Lines = "/api/records/hr.expense-line";
    private const string Actions = "/api/hr/expense-claims";

    [Fact]
    public async Task Employee_claims_expenses_manager_returns_and_approves_and_finance_marks_paid()
    {
        var s = await SetUpAsync();
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);
        using var finance = await LoginAsync(s.Finance);

        var claim = await PostAsync(staff, Claims, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["title"] = "Ankara ziyareti", ["currency"] = "try" });
        Assert.Equal(("draft", "TRY", s.Staff.Id.ToString()),
            (Text(claim, "status"), Text(claim, "currency"), Text(claim, "employeeUserId")));

        var taxi = await PostAsync(staff, Lines, Line(claim.Id, "2026-11-10", "Taxi", 100.50m));
        await PostAsync(staff, Lines, Line(claim.Id, "2026-11-10", "Hotel", 250m, vat: 25m));
        claim = await GetAsync(staff, $"{Claims}/{claim.Id}");
        Assert.Equal((350.50m, 2), (claim.Values["totalAmount"]!.GetValue<decimal>(), claim.Values["lineCount"]!.GetValue<int>()));

        // Gönderim: onaylayan o anki yöneticidir; yönetici astının beyanını ve satırlarını "kendi" kapsamıyla görür.
        var submitted = await ActAsync(staff, claim.Id, "submit", null);
        Assert.Equal(("submitted", s.Manager.Id.ToString()), (Text(submitted, "status"), Text(submitted, "approverUserId")));
        Assert.Contains(await ListAsync(manager, Claims, $"approverUserId={s.Manager.Id}&status=submitted"), c => c.Id == claim.Id);
        Assert.Equal(2, (await ListAsync(manager, Lines, $"claimId={claim.Id}")).Count);

        // İade: düzeltme için taslağa döner; çalışan satırı düzeltir, toplam güncellenir, yeniden gönderir.
        var returned = await ActAsync(manager, claim.Id, "return", "Taksi fişi eksik");
        Assert.Equal(("draft", "Taksi fişi eksik"), (Text(returned, "status"), Text(returned, "decisionReason")));
        var line = await GetAsync(staff, $"{Lines}/{taxi.Id}");
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"{Lines}/{taxi.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 80m }, line.Version))).StatusCode);
        Assert.Equal(330m, (await GetAsync(staff, $"{Claims}/{claim.Id}")).Values["totalAmount"]!.GetValue<decimal>());
        await ActAsync(staff, claim.Id, "submit", null);

        var approved = await ActAsync(manager, claim.Id, "approve", null);
        Assert.Equal(("approved", s.Manager.Id.ToString()), (Text(approved, "status"), Text(approved, "decidedByUserId")));

        // Finans onaylanan beyanı görür ve ödendi işaretler (referans isteğe bağlı).
        Assert.Contains(await ListAsync(finance, Claims, "status=approved"), c => c.Id == claim.Id);
        var paid = await ActAsync(finance, claim.Id, "pay", "DEK-2026-118");
        Assert.Equal(("paid", "DEK-2026-118", s.Finance.Id.ToString()),
            (Text(paid, "status"), Text(paid, "paymentReference"), Text(paid, "paidByUserId")));

        var history = (await finance.GetFromJsonAsync<AuditPageResponse>($"{Claims}/{claim.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));
    }

    [Fact]
    public async Task Invalid_claims_and_lines_are_rejected_and_only_drafts_change()
    {
        var s = await SetUpAsync();
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);
        using var hr = await LoginAsync(s.Hr);

        var badCurrency = await ValidationProblemAsync(await staff.PostAsJsonAsync(Claims,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["title"] = "X", ["currency"] = "T1" })));
        Assert.True(badCurrency.Errors.ContainsKey("currency"));

        var claim = await PostAsync(staff, Claims, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["title"] = "Kasım" });
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(staff, claim, "submit", null)).StatusCode); // satırsız gönderilmez

        var zero = await ValidationProblemAsync(await staff.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(claim.Id, "2026-11-10", "Taxi", 0m))));
        Assert.True(zero.Errors.ContainsKey("amount"));
        var vat = await ValidationProblemAsync(await staff.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(claim.Id, "2026-11-10", "Meal", 10m, vat: 20m))));
        Assert.True(vat.Errors.ContainsKey("vatAmount"));
        var future = await ValidationProblemAsync(await staff.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(claim.Id, "2099-01-01", "Meal", 10m))));
        Assert.True(future.Errors.ContainsKey("expenseDate"));

        var line = await PostAsync(staff, Lines, Line(claim.Id, "2026-11-11", "Meal", 45m));
        claim = await GetAsync(staff, $"{Claims}/{claim.Id}");
        var currencyChange = await ValidationProblemAsync(await staff.PutAsJsonAsync($"{Claims}/{claim.Id}",
            new RecordWriteRequest(new JsonObject { ["currency"] = "EUR" }, claim.Version)));
        Assert.True(currencyChange.Errors.ContainsKey("currency"));

        // Gönderildikten sonra beyan ve satırlar değişmez; beyan silinmez.
        var submitted = await ActAsync(staff, claim.Id, "submit", null);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PutAsJsonAsync($"{Claims}/{claim.Id}",
            new RecordWriteRequest(new JsonObject { ["title"] = "Değişti" }, submitted.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(claim.Id, "2026-11-12", "Taxi", 5m)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PutAsJsonAsync($"{Lines}/{line.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1m }, line.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"{Lines}/{line.Id}?version={line.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Claims}/{claim.Id}?version={submitted.Version}")).StatusCode);

        // Gerekçe isteyen eylemler; onaylanmamış beyan ödenmez; bilinmeyen eylem 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(manager, submitted, "reject", " ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(manager, submitted, "return", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(hr, submitted, "pay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(manager, submitted, "archive", null)).StatusCode);

        var rejected = await ActAsync(manager, claim.Id, "reject", "Politika dışı");
        Assert.Equal("rejected", Text(rejected, "status"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(staff, rejected, "cancel", null)).StatusCode);

        // Taslak beyan iptal edilir; satırı silinebilir ve toplam düşer.
        var draft = await PostAsync(staff, Claims, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["title"] = "Aralık" });
        var draftLine = await PostAsync(staff, Lines, Line(draft.Id, "2026-11-12", "Parking", 30m));
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"{Lines}/{draftLine.Id}?version={draftLine.Version}")).StatusCode);
        Assert.Equal(0m, (await GetAsync(staff, $"{Claims}/{draft.Id}")).Values["totalAmount"]!.GetValue<decimal>());
        Assert.Equal("cancelled", Text(await ActAsync(staff, draft.Id, "cancel", null), "status"));
    }

    [Fact]
    public async Task Another_tenants_claims_lines_and_actions_are_out_of_reach()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var staffA = await LoginAsync(a.Staff);
        using var hrB = await LoginAsync(b.Hr);
        var claimA = await PostAsync(staffA, Claims, new JsonObject { ["employeeId"] = a.StaffEmployee.ToString(), ["title"] = "A beyanı" });
        var lineA = await PostAsync(staffA, Lines, Line(claimA.Id, "2026-11-10", "Taxi", 12m));

        var foreignEmployee = await ValidationProblemAsync(await hrB.PostAsJsonAsync(Claims,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = a.StaffEmployee.ToString(), ["title"] = "Sızma" })));
        Assert.Equal(["Çalışan bulunamadı."], foreignEmployee.Errors["employeeId"]);
        var foreignClaim = await ValidationProblemAsync(await hrB.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(claimA.Id, "2026-11-10", "Taxi", 1m))));
        Assert.Equal(["Masraf beyanı bulunamadı."], foreignClaim.Errors["claimId"]);

        Assert.DoesNotContain(await ListAsync(hrB, Claims, null), c => c.Id == claimA.Id);
        Assert.DoesNotContain(await ListAsync(hrB, Lines, null), l => l.Id == lineA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Claims}/{claimA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Lines}/{lineA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Actions}/{claimA.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(hrB, claimA, "cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Staff_act_only_on_their_own_claims_managers_never_decide_their_own_and_finance_only_pays()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);
        using var finance = await LoginAsync(s.Finance);
        using var outsider = await LoginAsync(s.Outsider);

        var forOther = await ValidationProblemAsync(await staff.PostAsJsonAsync(Claims,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = s.ManagerEmployee.ToString(), ["title"] = "Başkası adına" })));
        Assert.Equal(["Yalnızca kendiniz için masraf beyanı açabilirsiniz."], forOther.Errors["employeeId"]);

        // Yöneticinin kendi beyanı: onaylayanı yok (yalnızca İK); kendisi karar veremez; çalışan göremez ve satır ekleyemez.
        var managersOwn = await PostAsync(manager, Claims, new JsonObject { ["employeeId"] = s.ManagerEmployee.ToString(), ["title"] = "Yönetici beyanı" });
        await PostAsync(manager, Lines, Line(managersOwn.Id, "2026-11-10", "Meal", 60m));
        var submitted = await ActAsync(manager, managersOwn.Id, "submit", null);
        Assert.Null(submitted.Values["approverUserId"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(manager, submitted, "approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Claims}/{managersOwn.Id}")).StatusCode);
        Assert.DoesNotContain(await ListAsync(staff, Claims, null), c => c.Id == managersOwn.Id);
        Assert.Empty(await ListAsync(staff, Lines, $"claimId={managersOwn.Id}"));
        Assert.False((await staff.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(managersOwn.Id, "2026-11-10", "Taxi", 1m)))).IsSuccessStatusCode);

        // Finans karar veremez (eylem görünmez, denenirse 403); İK onaylar; finans öder.
        Assert.DoesNotContain((await finance.GetFromJsonAsync<List<HrActionDto>>($"{Actions}/{managersOwn.Id}/actions"))!, a => a.Action == "approve");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(finance, submitted, "approve", null)).StatusCode);
        await ActAsync(hr, managersOwn.Id, "approve", null);
        Assert.Equal("paid", Text(await ActAsync(finance, managersOwn.Id, "pay", null), "status"));

        // Çalışan onay ve ödeme yapamaz.
        var own = await PostAsync(staff, Claims, new JsonObject { ["employeeId"] = s.StaffEmployee.ToString(), ["title"] = "Kendi" });
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, own, "approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, own, "pay", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Claims)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Lines)).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Claims)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(
        Guid TenantId, ModulixUser Hr, ModulixUser Manager, ModulixUser Staff, ModulixUser Finance, ModulixUser Outsider, Guid ManagerEmployee, Guid StaffEmployee);

    /// <summary>İK (yönetici rolü), yönetici ve çalışan ("kendi" kapsam, kartlarına bağlı), finans (tüm kapsam, ödeme) ve izinsiz kullanıcı.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var managerUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var financeUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, ExpensesRead, ExpensesRequest, ExpensesApprove], Scope.Own, managerUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, ExpensesRead, ExpensesRequest], Scope.Own, staffUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, ExpensesRead, ExpensesPay], Scope.All, financeUser);

        using var hr = await LoginAsync(hrUser);
        var managerCard = await EmployeeAsync(hr, "Melek", "Yönetir", managerUser.Id, managerEmployeeId: null);
        var staffCard = await EmployeeAsync(hr, "Sinan", "Çalışır", staffUser.Id, managerCard);
        return new Setup(tenant.Id, hrUser, managerUser, staffUser, financeUser, outsider, managerCard, staffCard);
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

    private static JsonObject Line(Guid claimId, string date, string type, decimal amount, decimal? vat = null) =>
        new() { ["claimId"] = claimId.ToString(), ["expenseDate"] = date, ["expenseTypeKey"] = type, ["amount"] = amount, ["vatAmount"] = vat };

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

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, RecordResponse claim, string action, string? reason) =>
        client.PostAsJsonAsync($"{Actions}/{claim.Id}/actions", new RunHrActionRequest(action, claim.Version, reason));

    /// <summary>Beyanın güncel sürümüyle eylemi uygular (satır değişiklikleri beyanın sürümünü artırır).</summary>
    private static async Task<RecordResponse> ActAsync(HttpClient client, Guid claimId, string action, string? reason)
    {
        var current = await GetAsync(client, $"{Claims}/{claimId}");
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
