using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 bölüm 20: görev geçmişi. Mutlu yol (kart açılınca ilk dönem; unvan/departman değişince önceki dönem kapanır, yenisi geçerlilik gününden
/// açılır; ilgisiz değişiklik dönem açmaz; ayrılınca son dönem kapanır, yeniden çalışınca açılır), doğrulama (geçerlilik günü önceki dönemin başından
/// sonra ve ayrılıştan önce olmalı; geçmiş elle yazılamaz, değişmez, silinmez), tenant izolasyonu ve yetki.
/// </summary>
public class HrEmployeePositionTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string Positions = "/api/records/hr.employee-position";

    [Fact]
    public async Task Position_changes_close_the_previous_period_and_open_a_new_one_from_the_effective_date()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var employee = await EmployeeAsync(admin, "Uzman", "Finance");

        var first = Assert.Single(await PositionsAsync(admin, employee));
        Assert.Equal(("2020-01-01", null, "Uzman", "Finance"), (Text(first, "startDate"), Text(first, "endDate"), Text(first, "jobTitle"), Text(first, "departmentKey")));

        await UpdateAsync(admin, employee, new JsonObject { ["jobTitle"] = "Kıdemli uzman", ["positionEffectiveDate"] = "2024-06-01" });
        await UpdateAsync(admin, employee, new JsonObject { ["notes"] = "Görevle ilgisiz değişiklik" });
        var periods = await PositionsAsync(admin, employee);
        Assert.Equal([("2024-06-01", null, "Kıdemli uzman"), ("2020-01-01", "2024-05-31", "Uzman")],
            periods.Select(p => (Text(p, "startDate"), Text(p, "endDate"), Text(p, "jobTitle"))));
        Assert.Equal(s.Admin.Id.ToString(), Text(periods[0], "changedByUserId"));

        // Ayrılınca son dönem ayrılış günüyle kapanır; yeniden çalışınca açılır.
        await UpdateAsync(admin, employee, new JsonObject { ["status"] = "terminated", ["terminationDate"] = "2025-12-31" });
        Assert.Equal("2025-12-31", Text((await PositionsAsync(admin, employee))[0], "endDate"));
        await UpdateAsync(admin, employee, new JsonObject { ["status"] = "active", ["terminationDate"] = null });
        Assert.Null(Text((await PositionsAsync(admin, employee))[0], "endDate"));
    }

    [Fact]
    public async Task Effective_dates_must_follow_the_previous_period_and_history_cannot_be_written_directly()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var employee = await EmployeeAsync(admin, "Uzman", "Finance");
        await UpdateAsync(admin, employee, new JsonObject { ["departmentKey"] = "Sales", ["positionEffectiveDate"] = "2024-01-01" });

        var tooEarly = await PutAsync(admin, employee, new JsonObject { ["departmentKey"] = "IT", ["positionEffectiveDate"] = "2024-01-01" });
        Assert.True((await ValidationProblemAsync(tooEarly)).Errors.ContainsKey("positionEffectiveDate"));
        var afterLeaving = await PutAsync(admin, employee, new JsonObject
        {
            ["departmentKey"] = "IT", ["positionEffectiveDate"] = "2026-02-01", ["status"] = "terminated", ["terminationDate"] = "2026-01-31",
        });
        Assert.True((await ValidationProblemAsync(afterLeaving)).Errors.ContainsKey("positionEffectiveDate"));
        Assert.Equal(2, (await PositionsAsync(admin, employee)).Count); // reddedilen değişiklik dönem yazmaz

        var row = (await PositionsAsync(admin, employee))[0];
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Positions,
            new RecordWriteRequest(new JsonObject { ["employeeId"] = employee.ToString() }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Positions}/{row.Id}",
            new RecordWriteRequest(new JsonObject(), row.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Positions}/{row.Id}?version={row.Version}")).StatusCode);
    }

    [Fact]
    public async Task Other_tenants_and_users_without_employee_access_cannot_read_the_history()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var outsider = await LoginAsync(a.Outsider);
        using var reader = await LoginAsync(a.Reader);
        var employee = await EmployeeAsync(adminA, "Uzman", "Finance");
        var row = Assert.Single(await PositionsAsync(adminA, employee));

        Assert.Empty((await adminB.GetFromJsonAsync<RecordPageResponse>(Positions))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Positions}/{row.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Positions)).StatusCode);

        // Yalnızca okuma izni olan kullanıcı geçmişi görür ama çalışan kartını (dolayısıyla görevi) değiştiremez.
        Assert.Single(await PositionsAsync(reader, employee));
        var current = (await reader.GetFromJsonAsync<RecordResponse>($"{Employees}/{employee}"))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Employees}/{employee}",
            new RecordWriteRequest(new JsonObject { ["jobTitle"] = "Müdür" }, current.Version))).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(ModulixUser Admin, ModulixUser Reader, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead], Scope.All, reader);
        return new Setup(admin, reader, outsider);
    }

    private static async Task<Guid> EmployeeAsync(HttpClient client, string title, string department)
    {
        var person = await PostAsync(client, "/api/records/core.person", new JsonObject { ["firstName"] = "Görev", ["lastName"] = "Sahibi" });
        return (await PostAsync(client, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["jobTitle"] = title, ["departmentKey"] = department,
        })).Id;
    }

    private static async Task<IReadOnlyList<RecordResponse>> PositionsAsync(HttpClient client, Guid employeeId) =>
        (await client.GetFromJsonAsync<RecordPageResponse>($"{Positions}?employeeId={employeeId}"))!.Items;

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid employeeId, JsonObject values)
    {
        var current = (await client.GetFromJsonAsync<RecordResponse>($"{Employees}/{employeeId}"))!;
        return await client.PutAsJsonAsync($"{Employees}/{employeeId}", new RecordWriteRequest(values, current.Version));
    }

    private static async Task UpdateAsync(HttpClient client, Guid employeeId, JsonObject values)
    {
        var response = await PutAsync(client, employeeId, values);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

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
