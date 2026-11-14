using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 bölüm 22: İK departmanı Core'un departman ağacıdır. Mutlu yol (seçici listesi; hesabı olmayan çalışana departman seçilir; hesabı bağlı
/// çalışan kullanıcının Core departmanını alır ve kullanıcı taşınınca kart kaydedildiğinde onu izler; eski İK değerleri adla eşlenir, duyuru hedefi
/// dahil), doğrulama (bulunmayan / kapalı departman, hesabı bağlı çalışana başka departman), tenant izolasyonu ve yetki.
/// </summary>
public class HrDepartmentTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string Announcements = "/api/records/hr.announcement";
    private const string Mapping = "/api/hr/department-mapping";

    [Fact]
    public async Task Employee_department_comes_from_the_core_tree_and_follows_the_linked_user()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var sales = await HrDepartments.CreateAsync(admin, "Satış");
        var istanbul = await HrDepartments.CreateAsync(admin, "İstanbul", sales);

        var units = (await admin.GetFromJsonAsync<List<HrBusinessUnitDto>>("/api/hr/business-units"))!;
        Assert.EndsWith("Satış › İstanbul", Assert.Single(units, u => u.Id == istanbul).PathName);

        // Hesabı olmayan çalışana departman seçilir; ad ağaç yoluyla okunur.
        var free = await EmployeeAsync(admin, new JsonObject { ["businessUnitId"] = istanbul.ToString() });
        Assert.Equal(istanbul.ToString(), Text(free, "businessUnitId"));
        Assert.EndsWith("Satış › İstanbul", Text(free, "businessUnitName"));
        Assert.False(free.Values["businessUnitFromAccount"]!.GetValue<bool>());

        // Hesabı bağlı çalışan kullanıcının Core departmanını alır (seçim gönderilmese de).
        await HrDepartments.MoveAsync(admin, factory, s.Staff.Id, s.TenantId, sales);
        var linked = await EmployeeAsync(admin, new JsonObject { ["userId"] = s.Staff.Id.ToString() });
        Assert.Equal(sales.ToString(), Text(linked, "businessUnitId"));
        Assert.True(linked.Values["businessUnitFromAccount"]!.GetValue<bool>());

        // Kullanıcı Core'da taşınınca kart bir sonraki kayıtta onu izler; görev geçmişine yeni dönem yazılır.
        await HrDepartments.MoveAsync(admin, factory, s.Staff.Id, s.TenantId, istanbul);
        var moved = await UpdateAsync(admin, linked.Id, new JsonObject { ["positionEffectiveDate"] = "2026-01-01" });
        Assert.Equal(istanbul.ToString(), Text(moved, "businessUnitId"));
        var periods = (await admin.GetFromJsonAsync<RecordPageResponse>($"/api/records/hr.employee-position?employeeId={linked.Id}"))!.Items;
        Assert.Equal([istanbul.ToString(), sales.ToString()], periods.Select(p => Text(p, "businessUnitId")));

        // Liste süzgeci departmana göre.
        Assert.Equal(new[] { free.Id, linked.Id }.Order(), (await admin.GetFromJsonAsync<RecordPageResponse>($"{Employees}?businessUnitId={istanbul}"))!.Items
            .Select(e => e.Id).Order());
    }

    [Fact]
    public async Task Legacy_department_values_are_mapped_to_core_departments_by_name()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var staff = await LoginAsync(s.Staff);
        var finance = await HrDepartments.CreateAsync(admin, "Finans ve Muhasebe");
        await HrDepartments.MoveAsync(admin, factory, s.Staff.Id, s.TenantId, finance);

        // Eski veriler: İK listesindeki departmanlar (Finans ve Muhasebe Core'da var, Satış ve Pazarlama yok) ve eski hedefli duyuru.
        var freeFinance = await EmployeeAsync(admin, new JsonObject { ["departmentKey"] = "Finance" });
        var freeSales = await EmployeeAsync(admin, new JsonObject { ["departmentKey"] = "Sales" });
        var announcement = await PostAsync(admin, Announcements, new JsonObject
        {
            ["title"] = "Finans duyurusu", ["body"] = "Metin", ["isPublished"] = true, ["publishOn"] = "2026-01-01",
        });
        await SetLegacyAudienceAsync(s.TenantId, announcement.Id, "Finance");
        await EmployeeAsync(admin, new JsonObject { ["userId"] = s.Staff.Id.ToString(), ["departmentKey"] = "Finance" });

        // Eşlenmemiş eski hedef, kartındaki eski departmana göre okura görünmeye devam eder.
        Assert.Contains((await staff.GetFromJsonAsync<RecordPageResponse>(Announcements))!.Items, a => a.Id == announcement.Id);

        var preview = (await admin.GetFromJsonAsync<List<DepartmentMappingRowDto>>(Mapping))!;
        var financeRow = Assert.Single(preview, r => r.DepartmentKey == "Finance");
        Assert.Equal((finance, 1, 1), (financeRow.BusinessUnitId!.Value, financeRow.EmployeeCount, financeRow.AnnouncementCount));
        var salesRow = Assert.Single(preview, r => r.DepartmentKey == "Sales");
        Assert.Null(salesRow.BusinessUnitId);

        var applied = await admin.PostAsJsonAsync(Mapping, new { });
        Assert.True(applied.StatusCode == HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        Assert.Equal(2, (await applied.Content.ReadFromJsonAsync<DepartmentMappingResultDto>())!.Updated);

        Assert.Equal(finance.ToString(), Text(await GetAsync(admin, $"{Employees}/{freeFinance.Id}"), "businessUnitId"));
        Assert.Null(Text(await GetAsync(admin, $"{Employees}/{freeSales.Id}"), "businessUnitId"));
        var mapped = await GetAsync(admin, $"{Announcements}/{announcement.Id}");
        Assert.Equal((finance.ToString(), (string?)null), (Text(mapped, "audienceBusinessUnitId"), Text(mapped, "audienceDepartmentKey")));
        Assert.Contains((await staff.GetFromJsonAsync<RecordPageResponse>(Announcements))!.Items, a => a.Id == announcement.Id);

        var after = (await admin.GetFromJsonAsync<List<DepartmentMappingRowDto>>(Mapping))!;
        Assert.Equal(0, after.Where(r => r.DepartmentKey == "Finance").Sum(r => r.EmployeeCount + r.AnnouncementCount));
        Assert.Equal(1, Assert.Single(after, r => r.DepartmentKey == "Sales").EmployeeCount);
    }

    [Fact]
    public async Task Unknown_closed_and_conflicting_departments_are_rejected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var closed = await HrDepartments.CreateAsync(admin, "Kapanan");
        var other = await HrDepartments.CreateAsync(admin, "Diğer");
        await HrDepartments.DeactivateAsync(admin, closed);
        var person = await PostAsync(admin, "/api/records/core.person", new JsonObject { ["firstName"] = "Deneme", ["lastName"] = "Kişi" });

        foreach (var unit in new[] { Guid.NewGuid(), closed })
        {
            var rejected = await admin.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
            {
                ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["businessUnitId"] = unit.ToString(),
            }));
            Assert.True((await ValidationProblemAsync(rejected)).Errors.ContainsKey("businessUnitId"));
        }

        // Hesabı bağlı çalışanın departmanı kullanıcınınkidir; kartta başka departman seçilemez.
        var linked = await EmployeeAsync(admin, new JsonObject { ["userId"] = s.Staff.Id.ToString() });
        var conflict = await PutAsync(admin, linked.Id, new JsonObject { ["businessUnitId"] = other.ToString() });
        Assert.True((await ValidationProblemAsync(conflict)).Errors.ContainsKey("businessUnitId"));
    }

    [Fact]
    public async Task Departments_and_mapping_stay_within_the_tenant_and_need_employee_permissions()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var reader = await LoginAsync(a.Reader);
        using var outsider = await LoginAsync(a.Outsider);
        var unitA = await HrDepartments.CreateAsync(adminA, "A departmanı");
        await EmployeeAsync(adminA, new JsonObject { ["departmentKey"] = "Finance" });

        // B, A'nın departmanını listede görmez ve kartına seçemez; A'nın eski verisi B'nin eşleme önizlemesinde yoktur.
        Assert.DoesNotContain((await adminB.GetFromJsonAsync<List<HrBusinessUnitDto>>("/api/hr/business-units"))!, u => u.Id == unitA);
        var person = await PostAsync(adminB, "/api/records/core.person", new JsonObject { ["firstName"] = "B", ["lastName"] = "Kişi" });
        var crossTenant = await adminB.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["businessUnitId"] = unitA.ToString(),
        }));
        Assert.True((await ValidationProblemAsync(crossTenant)).Errors.ContainsKey("businessUnitId"));
        Assert.DoesNotContain((await adminB.GetFromJsonAsync<List<DepartmentMappingRowDto>>(Mapping))!, r => r.EmployeeCount > 0);

        // Okur departmanları görür ama eşleme yapamaz; izinsiz kullanıcı departman listesini de göremez.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/hr/business-units")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Mapping)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Mapping, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/hr/business-units")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Staff, ModulixUser Reader, ModulixUser Outsider);

    /// <summary>Yönetici (İK dahil her izin), hesabı kartlara bağlanacak çalışan (duyuru okuma), yalnızca çalışan okuyan ve izinsiz kullanıcı.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var staff = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [AnnouncementsRead], Scope.Own, staff);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead], Scope.All, reader);
        return new Setup(tenant.Id, admin, staff, reader, outsider);
    }

    /// <summary>Bölüm 22'den önce yazılmış veri: duyurunun eski İK departman hedefi (genel uçtan artık yazılamaz).</summary>
    private async Task SetLegacyAudienceAsync(Guid tenantId, Guid announcementId, string departmentKey)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE hr.Announcements SET AudienceDepartmentKey = {departmentKey} WHERE Id = {announcementId} AND TenantId = {tenantId}");
    }

    private static async Task<RecordResponse> EmployeeAsync(HttpClient client, JsonObject values)
    {
        var person = await PostAsync(client, "/api/records/core.person", new JsonObject { ["firstName"] = "Departman", ["lastName"] = "Sahibi" });
        values["personId"] = person.Id.ToString();
        values["hireDate"] = "2020-01-01";
        return await PostAsync(client, Employees, values);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid employeeId, JsonObject values)
    {
        var current = await GetAsync(client, $"{Employees}/{employeeId}");
        return await client.PutAsJsonAsync($"{Employees}/{employeeId}", new RecordWriteRequest(values, current.Version));
    }

    private static async Task<RecordResponse> UpdateAsync(HttpClient client, Guid employeeId, JsonObject values)
    {
        var response = await PutAsync(client, employeeId, values);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
