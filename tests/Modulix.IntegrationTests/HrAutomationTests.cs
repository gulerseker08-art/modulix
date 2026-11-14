using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Hr.Automation;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Hr;
using Modulix.Shared.Notifications;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 bölüm 18: İK otomasyonu. Mutlu yol (görev atanınca sorumluya bildirim, son güne bir gün kala hatırlatma, çalışan ayrılınca işten çıkış
/// süreci), doğrulama (bütün şablonlar gerçek yayın doğrulamasından geçer; açık süreç varken ikinci süreç açılmaz), tenant izolasyonu (kural yalnızca
/// kendi firmasında çalışır) ve yetki (süreç yönetimi izni olmayan şablonu kullanamaz). Gerçek veritabanı, olay dağıtımı ve kural çalıştırıcısıyla.
/// </summary>
public class HrAutomationTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Templates = "/api/records/hr.checklist-template";
    private const string Items = "/api/records/hr.checklist-template-item";
    private const string Checklists = "/api/records/hr.employee-checklist";

    /// <summary>Tenant'ın bugünü (varsayılan saat dilimi İstanbul; tarih taraması tenant gününe göre çalışır, UTC gününe göre değil).</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fixture.Time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).DateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Every_hr_template_passes_real_publish_validation()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var listed = (await admin.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!.Where(t => t.ModuleKey == "hr").ToList();
        Assert.Equal(HrAutomationTemplates.All.Select(t => t.Key), listed.Select(t => t.Key));
        Assert.All(listed, t => Assert.True(t.Available, string.Join(";", t.Issues.Select(i => i.Message))));

        foreach (var template in HrAutomationTemplates.All)
        {
            var rule = await UseAsync(admin, template.Key);
            var publish = await admin.PostAsJsonAsync($"/api/automation/rules/{rule}/publish", new PublishAutomationRuleRequest(false, null));
            Assert.True(publish.StatusCode == HttpStatusCode.OK, $"{template.Key}: {await publish.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Assigned_tasks_notify_their_assignee_and_remind_a_day_before_the_due_date_only_in_their_own_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        using var staff = await LoginAsync(a.Staff);
        using var staffB = await LoginAsync(b.Staff);
        await PublishAsync(admin, HrAutomationTemplates.TaskAssigned);
        await PublishAsync(admin, HrAutomationTemplates.TaskDueTomorrow);

        // Çalışana yarın son günü olan bir görev, İK'ya (sorumlusuz) bir görev.
        var template = await TemplateAsync(admin, "onboarding", ("Hoş geldin eğitimi", "employee", 1), ("Bilgisayarı hazırla", "hr", 0));
        await StartAsync(admin, a.StaffEmployee, template, Today);
        await DeliverAndRunAsync(a.TenantId);

        var inbox = (await staff.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items.Select(n => n.Title).ToList();
        Assert.Contains($"Yeni görev: Hoş geldin eğitimi (son gün {Today.AddDays(1).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)})", inbox);
        Assert.Contains("Yarın son gün: Hoş geldin eğitimi", inbox);
        Assert.Equal(2, inbox.Count); // İK görevi sorumlusuz: kimseye bildirim gitmez

        // Yeniden tarama ikinci hatırlatma yazmaz; B firmasında kural yok, bildirim yok.
        await DeliverAndRunAsync(a.TenantId);
        Assert.Equal(2, (await staff.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items.Count);
        await DeliverAndRunAsync(b.TenantId);
        Assert.Empty((await staffB.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items);
    }

    [Fact]
    public async Task Terminating_an_employee_starts_one_offboarding_process_from_the_first_active_template()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await PublishAsync(admin, HrAutomationTemplates.OffboardingOnTermination);
        await TemplateAsync(admin, "offboarding", ("Zimmetleri teslim al", "hr", 0));
        var leaving = Today.AddDays(5);

        var current = await GetAsync(admin, $"{Employees}/{s.StaffEmployee}");
        var update = await admin.PutAsJsonAsync($"{Employees}/{s.StaffEmployee}", new RecordWriteRequest(
            new JsonObject { ["status"] = "terminated", ["terminationDate"] = Iso(leaving) }, current.Version));
        Assert.True(update.StatusCode == HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        await DeliverAndRunAsync(s.TenantId);

        var process = Assert.Single((await admin.GetFromJsonAsync<RecordPageResponse>($"{Checklists}?employeeId={s.StaffEmployee}"))!.Items);
        Assert.Equal(("offboarding", "open", Iso(leaving)),
            (process.Values["kind"]!.GetValue<string>(), process.Values["status"]!.GetValue<string>(), process.Values["startDate"]!.GetValue<string>()));

        // Kural yeniden tetiklense de (aktif → ayrıldı yeniden) açık süreç varken ikinci süreç açılmaz.
        var again = await GetAsync(admin, $"{Employees}/{s.StaffEmployee}");
        await admin.PutAsJsonAsync($"{Employees}/{s.StaffEmployee}", new RecordWriteRequest(new JsonObject { ["status"] = "active", ["terminationDate"] = null }, again.Version));
        var back = await GetAsync(admin, $"{Employees}/{s.StaffEmployee}");
        await admin.PutAsJsonAsync($"{Employees}/{s.StaffEmployee}", new RecordWriteRequest(
            new JsonObject { ["status"] = "terminated", ["terminationDate"] = Iso(leaving) }, back.Version));
        await DeliverAndRunAsync(s.TenantId);
        Assert.Single((await admin.GetFromJsonAsync<RecordPageResponse>($"{Checklists}?employeeId={s.StaffEmployee}"))!.Items);
    }

    [Fact]
    public async Task A_rule_manager_without_checklist_management_cannot_use_the_process_templates()
    {
        var s = await SetUpAsync();
        var ruleManager = await fixture.Factory.CreateUserAsync(s.Tenant);
        await fixture.Factory.CreateRoleAsync(s.TenantId,
            [Modulix.Core.Modules.CoreModule.Permissions.ReadAutomation, Modulix.Core.Modules.CoreModule.Permissions.ManageAutomation, EmployeesRead],
            Scope.All, ruleManager);
        using var client = await LoginAsync(ruleManager);

        var templates = (await client.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!;
        var offboarding = Assert.Single(templates, t => t.Key == HrAutomationTemplates.OffboardingOnTermination);
        Assert.False(offboarding.Available);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/automation/templates/{HrAutomationTemplates.OffboardingOnTermination}/use", null)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, Guid TenantId, ModulixUser Admin, ModulixUser Staff, Guid StaffEmployee);

    /// <summary>İK modülü açık firma: yönetici ve "kendi" kapsamında görev gören, kartı kullanıcısına bağlı çalışan.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        var staff = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        await fixture.Factory.CreateRoleAsync(tenant.Id, [EmployeesRead, ChecklistsRead], Scope.Own, staff);

        using var client = await LoginAsync(admin);
        var person = await PostAsync(client, People, new JsonObject { ["firstName"] = "Sinan", ["lastName"] = "Çalışır" });
        var employee = await PostAsync(client, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = staff.Id.ToString(),
        });
        return new Setup(tenant, tenant.Id, admin, staff, employee.Id);
    }

    private static async Task<Guid> UseAsync(HttpClient client, string templateKey)
    {
        var use = await client.PostAsync($"/api/automation/templates/{templateKey}/use", null);
        Assert.True(use.StatusCode == HttpStatusCode.Created, $"{templateKey}: {await use.Content.ReadAsStringAsync()}");
        return (await use.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!.Rule.Id;
    }

    private static async Task PublishAsync(HttpClient client, string templateKey)
    {
        var rule = await UseAsync(client, templateKey);
        var publish = await client.PostAsJsonAsync($"/api/automation/rules/{rule}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> TemplateAsync(HttpClient client, string kind, params (string Title, string Role, int Offset)[] steps)
    {
        var id = (await PostAsync(client, Templates, new JsonObject { ["name"] = $"{kind} şablonu", ["kind"] = kind })).Id;
        foreach (var step in steps)
        {
            await PostAsync(client, Items, new JsonObject
            {
                ["templateId"] = id.ToString(), ["title"] = step.Title, ["responsibleRole"] = step.Role, ["dueOffsetDays"] = step.Offset, ["taskType"] = "manual",
            });
        }

        return id;
    }

    private static async Task StartAsync(HttpClient client, Guid employeeId, Guid templateId, DateOnly day)
    {
        var response = await client.PostAsJsonAsync("/api/hr/employee-checklists/start", new StartChecklistRequest(employeeId, templateId, day));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Bekleyen olayları dağıtır (olay tetikleyicili kurallar çalışma açar), sonra kural turu çalışır (taramalar ve bekleyen çalışmalar).</summary>
    private async Task DeliverAndRunAsync(Guid tenantId)
    {
        for (var i = 0; i < 20; i++)
        {
            using var scope = fixture.Factory.OpenScope(tenantId);
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round.Dispatched == 0 && round.Delivered == 0)
            {
                break;
            }
        }

        var (_, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
