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
/// ADR-0042 dilim 5: işe giriş/çıkış. Mutlu yol (şablon ve adımlar, süreç başlatma, görevlerin sorumlu ve son tarihleri, çalışanın, yöneticinin ve
/// İK'nın görevleri, ilerleme ve tamamlanma, yeniden açma ve atlama), doğrulama (zimmet iadesi yalnızca çıkışta, açık zimmetle çıkış görevi
/// kapanmaz, pasif/boş şablon, ayrılmış çalışana işe giriş, mükerrer açık süreç, süreç ve görev yalnızca komutla değişir, silme yasakları,
/// gerekçe), tenant izolasyonu ve yetki (çalışan yalnızca kendi süreci ve kendi görevi; şablon ve süreç yönetimi İK'nın).
/// </summary>
public class HrChecklistTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Templates = "/api/records/hr.checklist-template";
    private const string Items = "/api/records/hr.checklist-template-item";
    private const string Checklists = "/api/records/hr.employee-checklist";
    private const string Tasks = "/api/records/hr.checklist-task";
    private const string Start = "/api/hr/employee-checklists/start";

    [Fact]
    public async Task Onboarding_creates_tasks_for_employee_manager_and_hr_and_completes_when_all_are_closed()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var manager = await LoginAsync(s.Manager);

        var template = await TemplateAsync(hr, "Standart işe giriş", "onboarding",
            ("Hoş geldin eğitimi", "employee", 0, "manual"), ("Bilgisayarı hazırla", "hr", -2, "manual"), ("Tanışma toplantısı", "manager", 1, "manual"));

        var checklist = await StartAsync(hr, s.StaffEmployee, template, "2026-12-01");
        Assert.Equal(("onboarding", "Standart işe giriş", "open", 3, 0),
            (Text(checklist, "kind"), Text(checklist, "name"), Text(checklist, "status"), Number(checklist, "taskCount"), Number(checklist, "closedCount")));

        var tasks = await ListAsync(hr, Tasks, $"checklistId={checklist.Id}&sort=sequence");
        Assert.Equal(["2026-12-01", "2026-11-29", "2026-12-02"], tasks.Select(t => Text(t, "dueDate")));
        Assert.Equal([s.Staff.Id.ToString(), null, s.Manager.Id.ToString()], tasks.Select(t => Text(t, "assigneeUserId")));

        // Çalışan kendi görevini görür ve tamamlar; İK görevini tamamlayamaz. Yönetici kendi görevini tamamlar.
        var staffTask = Assert.Single(await ListAsync(staff, Tasks, $"assigneeUserId={s.Staff.Id}&status=open"));
        Assert.Equal("done", Text(await ActAsync(staff, staffTask.Id, "complete", "Tamamlandı"), "status"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, tasks[1], "complete", null)).StatusCode);
        Assert.Contains(await ListAsync(manager, Checklists, null), c => c.Id == checklist.Id); // görev atanan süreci görür
        await ActAsync(manager, tasks[2].Id, "complete", null);
        Assert.Equal(("open", 2), (Text(await GetAsync(hr, $"{Checklists}/{checklist.Id}"), "status"), Number(await GetAsync(hr, $"{Checklists}/{checklist.Id}"), "closedCount")));

        // Son görev kapanınca süreç tamamlanır; yeniden açılınca açılır; atlanınca yine tamamlanır.
        await ActAsync(hr, tasks[1].Id, "complete", null);
        var completed = await GetAsync(hr, $"{Checklists}/{checklist.Id}");
        Assert.Equal(("completed", 3), (Text(completed, "status"), Number(completed, "closedCount")));
        Assert.NotNull(completed.Values["completedAt"]);
        await ActAsync(hr, tasks[1].Id, "reopen", null);
        Assert.Equal("open", Text(await GetAsync(hr, $"{Checklists}/{checklist.Id}"), "status"));
        Assert.Equal("skipped", Text(await ActAsync(hr, tasks[1].Id, "skip", "Bilgisayar mevcut"), "status"));
        Assert.Equal("completed", Text(await GetAsync(staff, $"{Checklists}/{checklist.Id}"), "status")); // çalışan kendi sürecini görür
    }

    [Fact]
    public async Task Offboarding_cannot_close_with_open_assets_and_invalid_starts_and_direct_writes_are_rejected()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);

        // Şablon kuralları.
        var onboarding = await TemplateAsync(hr, "Giriş", "onboarding", ("Eğitim", "employee", 0, "manual"));
        var wrongType = await ValidationProblemAsync(await hr.PostAsJsonAsync(Items, new RecordWriteRequest(Item(onboarding, "Zimmet iadesi", "hr", 0, "assetReturn"))));
        Assert.True(wrongType.Errors.ContainsKey("taskType"));
        var sameName = await ValidationProblemAsync(await hr.PostAsJsonAsync(Templates,
            new RecordWriteRequest(new JsonObject { ["name"] = "Giriş", ["kind"] = "onboarding" })));
        Assert.True(sameName.Errors.ContainsKey("name"));
        var empty = (await PostAsync(hr, Templates, new JsonObject { ["name"] = "Boş", ["kind"] = "onboarding" })).Id;
        var inactive = await TemplateAsync(hr, "Eski", "onboarding", ("X", "hr", 0, "manual"));
        var inactiveRecord = await GetAsync(hr, $"{Templates}/{inactive}");
        Assert.Equal(HttpStatusCode.OK, (await hr.PutAsJsonAsync($"{Templates}/{inactive}",
            new RecordWriteRequest(new JsonObject { ["isActive"] = false }, inactiveRecord.Version))).StatusCode);

        // Başlatma kuralları.
        Assert.True((await StartProblemAsync(hr, s.StaffEmployee, empty, "2026-12-01")).Errors.ContainsKey("templateId"));
        Assert.True((await StartProblemAsync(hr, s.StaffEmployee, inactive, "2026-12-01")).Errors.ContainsKey("templateId"));
        Assert.True((await StartProblemAsync(hr, s.LeftEmployee, onboarding, "2026-12-01")).Errors.ContainsKey("employeeId"));
        await StartAsync(hr, s.StaffEmployee, onboarding, "2026-12-01");
        Assert.Contains("açık bir işe giriş", (await StartProblemAsync(hr, s.StaffEmployee, onboarding, "2026-12-02")).Errors["employeeId"].Single());

        // Süreç ve görev genel uçtan açılmaz, değişmez, silinmez; kullanılmış şablon silinmez, kullanılmamış adımlarıyla silinir.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync(Checklists, new RecordWriteRequest(new JsonObject
        {
            ["employeeId"] = s.StaffEmployee.ToString(), ["templateId"] = onboarding.ToString(), ["startDate"] = "2026-12-01",
        }))).StatusCode);
        var anyTask = (await ListAsync(hr, Tasks, null)).First();
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PutAsJsonAsync($"{Tasks}/{anyTask.Id}",
            new RecordWriteRequest(new JsonObject { ["dueDate"] = "2027-01-01" }, anyTask.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Tasks}/{anyTask.Id}?version={anyTask.Version}")).StatusCode);
        var onboardingRecord = await GetAsync(hr, $"{Templates}/{onboarding}");
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Templates}/{onboarding}?version={onboardingRecord.Version}")).StatusCode);
        var emptyRecord = await GetAsync(hr, $"{Templates}/{empty}");
        Assert.Equal(HttpStatusCode.NoContent, (await hr.DeleteAsync($"{Templates}/{empty}?version={emptyRecord.Version}")).StatusCode);

        // Çıkış: açık zimmet varken "zimmet iadesi" tamamlanmaz; iade alınınca tamamlanır ve süreç kapanır.
        var offboarding = await TemplateAsync(hr, "Çıkış", "offboarding", ("Zimmetleri iade al", "hr", 0, "assetReturn"), ("Hesabı kapat", "hr", 0, "manual"));
        var asset = await PostAsync(hr, "/api/records/hr.asset", new JsonObject { ["assetTag"] = "LT-9", ["name"] = "Dizüstü", ["categoryKey"] = "Laptop" });
        var assignment = await PostAsync(hr, "/api/records/hr.asset-assignment", new JsonObject
        {
            ["assetId"] = asset.Id.ToString(), ["employeeId"] = s.StaffEmployee.ToString(), ["assignedOn"] = "2026-11-02",
        });
        var exit = await StartAsync(hr, s.StaffEmployee, offboarding, "2026-12-31");
        var exitTasks = await ListAsync(hr, Tasks, $"checklistId={exit.Id}&sort=sequence");
        var blocked = await PostActionAsync(hr, exitTasks[0], "complete", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("açık zimmeti", await blocked.Content.ReadAsStringAsync());
        var actions = (await hr.GetFromJsonAsync<List<HrActionDto>>($"/api/hr/checklist-tasks/{exitTasks[0].Id}/actions"))!;
        Assert.False(actions.Single(a => a.Action == "complete").Allowed);

        var current = await GetAsync(hr, $"/api/records/hr.asset-assignment/{assignment.Id}");
        Assert.Equal(HttpStatusCode.OK, (await hr.PostAsJsonAsync($"/api/hr/asset-assignments/{assignment.Id}/actions",
            new RunHrActionRequest("return", current.Version, null))).StatusCode);
        await ActAsync(hr, exitTasks[0].Id, "complete", null);
        await ActAsync(hr, exitTasks[1].Id, "complete", null);
        var closed = await GetAsync(hr, $"{Checklists}/{exit.Id}");
        Assert.Equal("completed", Text(closed, "status"));

        // Gerekçe isteyen eylemler; tamamlanmış süreç iptal edilmez; iptal edilmiş sürecin görevi değişmez.
        Assert.Equal(HttpStatusCode.Conflict, (await PostChecklistActionAsync(hr, closed, "cancel", "Vazgeçildi")).StatusCode);
        var second = await StartAsync(hr, s.OtherEmployee, offboarding, "2026-12-31");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostChecklistActionAsync(hr, second, "cancel", " ")).StatusCode);
        var secondTask = (await ListAsync(hr, Tasks, $"checklistId={second.Id}&sort=sequence"))[1];
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(hr, secondTask, "skip", null)).StatusCode);
        var cancelled = await hr.PostAsJsonAsync($"/api/hr/employee-checklists/{second.Id}/actions", new RunHrActionRequest("cancel", second.Version, "Çalışan kaldı"));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(hr, secondTask, "complete", null)).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_processes_tasks_and_templates_are_out_of_reach()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hrA = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        var templateA = await TemplateAsync(hrA, "A şablonu", "onboarding", ("Eğitim", "hr", 0, "manual"));
        var checklistA = await StartAsync(hrA, a.StaffEmployee, templateA, "2026-12-01");
        var taskA = (await ListAsync(hrA, Tasks, $"checklistId={checklistA.Id}")).Single();

        Assert.Equal(["Çalışan bulunamadı."], (await StartProblemAsync(hrB, a.StaffEmployee, templateA, "2026-12-01")).Errors["employeeId"]);
        Assert.Equal(["Şablon bulunamadı."], (await StartProblemAsync(hrB, b.StaffEmployee, templateA, "2026-12-01")).Errors["templateId"]);
        var foreignItem = await ValidationProblemAsync(await hrB.PostAsJsonAsync(Items, new RecordWriteRequest(Item(templateA, "Sızma", "hr", 0, "manual"))));
        Assert.Equal(["Şablon bulunamadı."], foreignItem.Errors["templateId"]);

        Assert.DoesNotContain(await ListAsync(hrB, Checklists, null), c => c.Id == checklistA.Id);
        Assert.DoesNotContain(await ListAsync(hrB, Tasks, null), t => t.Id == taskA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Checklists}/{checklistA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"/api/hr/checklist-tasks/{taskA.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(hrB, taskA, "complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostChecklistActionAsync(hrB, checklistA, "cancel", "Sızma")).StatusCode);

        // Aynı şablon adı başka tenant'ta serbesttir.
        await TemplateAsync(hrB, "A şablonu", "onboarding", ("Eğitim", "hr", 0, "manual"));
    }

    [Fact]
    public async Task Staff_only_see_their_own_process_and_complete_their_own_tasks()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var outsider = await LoginAsync(s.Outsider);
        var template = await TemplateAsync(hr, "Giriş", "onboarding", ("Kendi görevi", "employee", 0, "manual"), ("İK görevi", "hr", 0, "manual"));
        var mine = await StartAsync(hr, s.StaffEmployee, template, "2026-12-01");
        var others = await StartAsync(hr, s.OtherEmployee, template, "2026-12-01");

        // Şablon ve süreç yönetimi İK'nındır.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Templates,
            new RecordWriteRequest(new JsonObject { ["name"] = "Benim", ["kind"] = "onboarding" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Start, new StartChecklistRequest(s.StaffEmployee, template, new DateOnly(2027, 1, 1)))).StatusCode);

        // Kendi süreci ve görevleri görünür; başkasınınki görünmez.
        Assert.Equal([mine.Id], (await ListAsync(staff, Checklists, null)).Select(c => c.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Checklists}/{others.Id}")).StatusCode);
        Assert.All(await ListAsync(staff, Tasks, null), t => Assert.Equal(mine.Id.ToString(), Text(t, "checklistId")));
        var othersTask = (await ListAsync(hr, Tasks, $"checklistId={others.Id}")).First();
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(staff, othersTask, "complete", null)).StatusCode);

        // Kendi görevini tamamlar ama atlayamaz ve yeniden açamaz; İK görevine dokunamaz.
        var myTasks = await ListAsync(staff, Tasks, $"checklistId={mine.Id}&sort=sequence");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, myTasks[0], "skip", "Gerek yok")).StatusCode);
        var done = await ActAsync(staff, myTasks[0].Id, "complete", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, done, "reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, myTasks[1], "complete", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Checklists)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Tasks)).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Checklists)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(
        Guid TenantId, ModulixUser Hr, ModulixUser Manager, ModulixUser Staff, ModulixUser Outsider, Guid StaffEmployee, Guid OtherEmployee, Guid LeftEmployee);

    /// <summary>İK (yönetici rolü), yönetici ve çalışan ("kendi" kapsam, kartlarına bağlı; çalışanın yöneticisi yönetici), izinsiz kullanıcı.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var managerUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, ChecklistsRead], Scope.Own, managerUser, staffUser);

        using var hr = await LoginAsync(hrUser);
        var managerCard = await EmployeeAsync(hr, "Melek", "Yönetir", managerUser.Id, null, terminated: false);
        var staffCard = await EmployeeAsync(hr, "Sinan", "Çalışır", staffUser.Id, managerCard, terminated: false);
        var otherCard = await EmployeeAsync(hr, "Deniz", "Başka", null, managerCard, terminated: false);
        var leftCard = await EmployeeAsync(hr, "Ece", "Ayrıldı", null, null, terminated: true);
        return new Setup(tenant.Id, hrUser, managerUser, staffUser, outsider, staffCard, otherCard, leftCard);
    }

    private static async Task<Guid> EmployeeAsync(HttpClient client, string first, string last, Guid? userId, Guid? managerId, bool terminated)
    {
        var person = await PostAsync(client, People, new JsonObject { ["firstName"] = first, ["lastName"] = last });
        return (await PostAsync(client, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = userId?.ToString(), ["managerEmployeeId"] = managerId?.ToString(),
            ["status"] = terminated ? "terminated" : "active", ["terminationDate"] = terminated ? "2025-12-31" : null,
        })).Id;
    }

    private static async Task<Guid> TemplateAsync(HttpClient client, string name, string kind, params (string Title, string Role, int Offset, string Type)[] steps)
    {
        var id = (await PostAsync(client, Templates, new JsonObject { ["name"] = name, ["kind"] = kind })).Id;
        foreach (var step in steps)
        {
            await PostAsync(client, Items, Item(id, step.Title, step.Role, step.Offset, step.Type));
        }

        return id;
    }

    private static JsonObject Item(Guid templateId, string title, string role, int offset, string type) => new()
    {
        ["templateId"] = templateId.ToString(), ["title"] = title, ["responsibleRole"] = role, ["dueOffsetDays"] = offset, ["taskType"] = type,
    };

    private static async Task<RecordResponse> StartAsync(HttpClient client, Guid employeeId, Guid templateId, string day)
    {
        var response = await client.PostAsJsonAsync(Start, new StartChecklistRequest(employeeId, templateId, DateOnly.Parse(day)));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> StartProblemAsync(HttpClient client, Guid employeeId, Guid templateId, string day) =>
        await ValidationProblemAsync(await client.PostAsJsonAsync(Start, new StartChecklistRequest(employeeId, templateId, DateOnly.Parse(day))));

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

    private static int Number(RecordResponse record, string field) => record.Values[field]!.GetValue<int>();

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

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, RecordResponse task, string action, string? reason) =>
        client.PostAsJsonAsync($"/api/hr/checklist-tasks/{task.Id}/actions", new RunHrActionRequest(action, task.Version, reason));

    private static async Task<HttpResponseMessage> PostChecklistActionAsync(HttpClient client, RecordResponse checklist, string action, string? reason)
    {
        var current = await client.GetAsync($"{Checklists}/{checklist.Id}");
        var version = current.IsSuccessStatusCode ? (await current.Content.ReadFromJsonAsync<RecordResponse>())!.Version : checklist.Version;
        return await client.PostAsJsonAsync($"/api/hr/employee-checklists/{checklist.Id}/actions", new RunHrActionRequest(action, version, reason));
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, Guid taskId, string action, string? reason)
    {
        var current = await GetAsync(client, $"{Tasks}/{taskId}");
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
