using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 dilim 1: çalışan kartı (özlük), rehber ve organizasyon şeması verisi. Mutlu yol (kişi açıp kart, kişi kaydından gelen ad ve
/// iletişim, yönetici bağı, ada göre sıralama ve arama, "ayrılmamış" süzgeci, denetim kaydı), doğrulama (kişi başına tek kart, sicil no
/// ve kullanıcı benzersizliği, T.C. kimlik no ve IBAN denetimi, ayrılış tarihi kuralları, yönetici döngüsü, silme yasağı), tenant izolasyonu
/// (başka tenant'ın kişisi, çalışanı ve yöneticisi kullanılamaz ve görünmez) ve yetki (rehber izni özlük bilgisini göremez ve yazamaz,
/// okuyan yazamaz, izinsiz göremez, modül kapalıyken kullanılamaz).
/// </summary>
public class HrEmployeeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string ValidNationalId = "10000000146";
    private const string ValidIban = "TR330006100519786457841326";

    [Fact]
    public async Task Employees_are_people_with_job_and_manager_data_and_are_listed_searched_and_audited()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var ayse = await PersonAsync(admin, "Ayşe", "Demir", "0532 100 00 01", "ayse@ornek.com");
        var manager = await PostAsync(admin, Employees, new JsonObject
        {
            ["personId"] = ayse.ToString(), ["employeeNumber"] = "s-001", ["jobTitle"] = "Genel müdür", ["departmentKey"] = "Management",
            ["hireDate"] = "2020-01-06", ["nationalId"] = ValidNationalId, ["iban"] = "TR33 0006 1005 1978 6457 8413 26",
        });
        Assert.Equal("Ayşe Demir", manager.Values["fullName"]!.GetValue<string>());
        Assert.Equal("ayse@ornek.com", manager.Values["email"]!.GetValue<string>());
        Assert.Equal("S-001", manager.Values["employeeNumber"]!.GetValue<string>());
        Assert.Equal(ValidIban, manager.Values["iban"]!.GetValue<string>());
        Assert.Equal("active", manager.Values["status"]!.GetValue<string>());
        Assert.Equal("fullTime", manager.Values["employmentType"]!.GetValue<string>());

        var burak = await PersonAsync(admin, "Burak", "Kaya", null, null);
        var report = await PostAsync(admin, Employees, new JsonObject
        {
            ["personId"] = burak.ToString(), ["jobTitle"] = "Muhasebe uzmanı", ["departmentKey"] = "Finance", ["hireDate"] = "2023-03-01",
            ["managerEmployeeId"] = manager.Id.ToString(), ["userId"] = s.Reader.Id.ToString(),
        });

        Assert.Equal([manager.Id, report.Id], (await ListAsync(admin, "sort=name")).Select(e => e.Id)); // Ayşe, Burak
        Assert.Equal([report.Id], (await ListAsync(admin, "search=Burak Ka")).Select(e => e.Id));
        Assert.Equal([manager.Id], (await ListAsync(admin, "search=0532")).Select(e => e.Id));
        Assert.Equal([manager.Id], (await ListAsync(admin, "search=s-001")).Select(e => e.Id));
        Assert.Equal([report.Id], (await ListAsync(admin, $"managerEmployeeId={manager.Id}")).Select(e => e.Id));
        Assert.Equal([report.Id], (await ListAsync(admin, "departmentKey=Finance")).Select(e => e.Id));
        Assert.Equal([report.Id], (await ListAsync(admin, $"userId={s.Reader.Id}")).Select(e => e.Id));

        // Ayrılan çalışan "ayrılmamış" (rehber, şema) listesinden düşer ama kartı kalır.
        var left = await PutAsync(admin, report, new JsonObject { ["status"] = "terminated", ["terminationDate"] = "2026-10-31" });
        Assert.Equal([manager.Id], (await ListAsync(admin, "current=true")).Select(e => e.Id));
        Assert.Equal([report.Id], (await ListAsync(admin, "status=terminated")).Select(e => e.Id));
        Assert.Equal("2026-10-31", left.Values["terminationDate"]!.GetValue<string>());

        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Employees}/{report.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));

        // Kişi kaydı çalışan kartında kullanıldığı için silinemez.
        var person = (await admin.GetFromJsonAsync<RecordResponse>($"{People}/{ayse}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{People}/{ayse}?version={person.Version}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_employee_cards_are_rejected_and_cards_are_never_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var first = await PostAsync(admin, Employees, new JsonObject
        {
            ["personId"] = (await PersonAsync(admin, "Cem", "Ak", null, null)).ToString(), ["hireDate"] = "2024-01-01",
            ["employeeNumber"] = "100", ["userId"] = s.Reader.Id.ToString(), ["nationalId"] = ValidNationalId,
        });

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("personId"));
        Assert.True(missing.Errors.ContainsKey("hireDate"));

        var duplicatePerson = await ValidationProblemAsync(await admin.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = (await admin.GetFromJsonAsync<RecordResponse>($"{Employees}/{first.Id}"))!.Values["personId"]!.GetValue<string>(), ["hireDate"] = "2024-01-01",
        })));
        Assert.Equal(["Bu kişinin zaten bir çalışan kartı var."], duplicatePerson.Errors["personId"]);

        var other = await PersonAsync(admin, "Deniz", "Er", null, null);
        var duplicates = await ValidationProblemAsync(await admin.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = other.ToString(), ["hireDate"] = "2024-01-01", ["employeeNumber"] = "100", ["userId"] = s.Reader.Id.ToString(),
            ["nationalId"] = ValidNationalId,
        })));
        Assert.Equal(["Bu sicil numarası başka bir çalışanda kayıtlı."], duplicates.Errors["employeeNumber"]);
        Assert.Equal(["Bu kullanıcı başka bir çalışan kartına bağlı."], duplicates.Errors["userId"]);
        Assert.Equal(["Bu T.C. kimlik numarası başka bir çalışanda kayıtlı."], duplicates.Errors["nationalId"]);

        var badData = await ValidationProblemAsync(await admin.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = other.ToString(), ["hireDate"] = "2024-01-01", ["nationalId"] = "10000000147", ["iban"] = "TR330006100519786457841327",
            ["status"] = "terminated", ["birthDate"] = "2025-01-01",
        })));
        Assert.StartsWith("T.C. kimlik numarası", badData.Errors["nationalId"].Single());
        Assert.StartsWith("IBAN", badData.Errors["iban"].Single());
        Assert.Contains("ayrılış tarihi girilmelidir", badData.Errors["terminationDate"].Single());
        Assert.Equal(["Doğum tarihi işe giriş tarihinden önce olmalıdır."], badData.Errors["birthDate"]);

        var leftBeforeHire = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Employees}/{first.Id}",
            new RecordWriteRequest(new JsonObject { ["terminationDate"] = "2023-12-31" }, first.Version)));
        Assert.Equal(["Ayrılış tarihi işe giriş tarihinden önce olamaz."], leftBeforeHire.Errors["terminationDate"]);

        // Yönetici: kendisi olamaz, astı olamaz (döngü), bilinmeyen çalışan bulunamaz.
        var report = await PostAsync(admin, Employees, new JsonObject
        {
            ["personId"] = other.ToString(), ["hireDate"] = "2024-02-01", ["managerEmployeeId"] = first.Id.ToString(),
        });
        foreach (var (managerId, expected) in new[] { (first.Id, "kendi yöneticisi"), (report.Id, "döngü"), (Guid.NewGuid(), "Yönetici bulunamadı") })
        {
            var rejected = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Employees}/{first.Id}",
                new RecordWriteRequest(new JsonObject { ["managerEmployeeId"] = managerId.ToString() }, first.Version)));
            Assert.Contains(expected, rejected.Errors["managerEmployeeId"].Single());
        }

        // Kullanıcı hesabı bu kuruluşun etkin üyesi olmalıdır.
        var stranger = await factory.CreateUserAsync(await factory.CreateTenantAsync());
        var notMember = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Employees}/{report.Id}",
            new RecordWriteRequest(new JsonObject { ["userId"] = stranger.Id.ToString() }, report.Version)));
        Assert.True(notMember.Errors.ContainsKey("userId"));

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Employees}/{report.Id}?version={report.Version}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_people_and_employees_cannot_be_used_or_seen()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var personA = await PersonAsync(adminA, "Ali", "Kaya", null, null);
        var employeeA = await PostAsync(adminA, Employees, new JsonObject
        {
            ["personId"] = personA.ToString(), ["hireDate"] = "2022-05-01", ["employeeNumber"] = "A-1", ["nationalId"] = ValidNationalId,
        });

        var foreignPerson = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Employees,
            new RecordWriteRequest(new JsonObject { ["personId"] = personA.ToString(), ["hireDate"] = "2022-05-01" })));
        Assert.Equal(["Kişi bulunamadı."], foreignPerson.Errors["personId"]);

        // Aynı sicil numarası ve T.C. kimlik numarası başka tenant'ta serbesttir; yönetici olarak A'nın çalışanı seçilemez.
        var personB = await PersonAsync(adminB, "Ali", "Kaya", null, null);
        var foreignManager = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Employees, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = personB.ToString(), ["hireDate"] = "2022-05-01", ["managerEmployeeId"] = employeeA.Id.ToString(),
        })));
        Assert.Equal(["Yönetici bulunamadı."], foreignManager.Errors["managerEmployeeId"]);
        await PostAsync(adminB, Employees, new JsonObject
        {
            ["personId"] = personB.ToString(), ["hireDate"] = "2022-05-01", ["employeeNumber"] = "A-1", ["nationalId"] = ValidNationalId,
        });

        Assert.DoesNotContain(await ListAsync(adminB, null), e => e.Id == employeeA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Employees}/{employeeA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Employees}/{employeeA.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "terminated", ["terminationDate"] = "2026-01-01" }, employeeA.Version))).StatusCode);
    }

    [Fact]
    public async Task Directory_readers_do_not_see_or_change_personal_data_and_outsiders_cannot_read()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var outsider = await LoginAsync(s.Outsider);
        var employee = await PostAsync(admin, Employees, new JsonObject
        {
            ["personId"] = (await PersonAsync(admin, "Okan", "Tan", "0555 000 00 01", null)).ToString(), ["hireDate"] = "2021-09-01",
            ["jobTitle"] = "Satış temsilcisi", ["nationalId"] = ValidNationalId, ["iban"] = ValidIban, ["birthDate"] = "1990-04-12",
        });

        // Rehber izni: ad, unvan ve iletişim görünür; özlük alanları yanıtta yoktur, listede de yoktur.
        var seen = (await reader.GetFromJsonAsync<RecordResponse>($"{Employees}/{employee.Id}"))!;
        Assert.Equal("Okan Tan", seen.Values["fullName"]!.GetValue<string>());
        Assert.Equal("Satış temsilcisi", seen.Values["jobTitle"]!.GetValue<string>());
        Assert.Equal("0555 000 00 01", seen.Values["phone"]!.GetValue<string>());
        foreach (var field in new[] { "nationalId", "iban", "birthDate", "homeAddress", "emergencyContactName", "emergencyContactPhone" })
        {
            Assert.False(seen.Values.ContainsKey(field), field);
        }

        Assert.All(await ListAsync(reader, null), e => Assert.False(e.Values.ContainsKey("nationalId")));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Employees}/{employee.Id}",
            new RecordWriteRequest(new JsonObject { ["jobTitle"] = "Değişti" }, employee.Version))).StatusCode);

        // Çalışan yazma izni özlük izni olmadan özlük alanını değiştiremez; diğer alanları değiştirebilir ve özlük değeri korunur.
        var personalAttempt = await writer.PutAsJsonAsync($"{Employees}/{employee.Id}",
            new RecordWriteRequest(new JsonObject { ["iban"] = ValidIban, ["nationalId"] = "12345678950" }, employee.Version));
        Assert.False(personalAttempt.IsSuccessStatusCode, $"{(int)personalAttempt.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, (await writer.PutAsJsonAsync($"{Employees}/{employee.Id}",
            new RecordWriteRequest(new JsonObject { ["jobTitle"] = "Satış müdürü" }, employee.Version))).StatusCode);
        var unchanged = (await admin.GetFromJsonAsync<RecordResponse>($"{Employees}/{employee.Id}"))!;
        Assert.Equal(ValidNationalId, unchanged.Values["nationalId"]!.GetValue<string>());
        Assert.Equal("Satış müdürü", unchanged.Values["jobTitle"]!.GetValue<string>());

        // Denetim kaydında özlük değeri izni olmayana görünmez.
        var writerHistory = await (await writer.GetAsync($"{Employees}/{employee.Id}/history")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(ValidIban, writerHistory);

        // Çalışan yazma izni, CRM izni olmadan çalışan için kişi açmaya yeter.
        var person = await PersonAsync(writer, "Selin", "Ak", null, null);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Employees,
            new RecordWriteRequest(new JsonObject { ["personId"] = person.ToString(), ["hireDate"] = "2026-11-01" }))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Employees)).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Employees)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [EmployeesRead, EmployeesWrite], Scope.All, writer);
        return new Setup(tenant.Id, admin, reader, writer, outsider);
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

    private static async Task<Guid> PersonAsync(HttpClient client, string first, string last, string? phone, string? email) =>
        (await PostAsync(client, People, new JsonObject { ["firstName"] = first, ["lastName"] = last, ["phone"] = phone, ["email"] = email })).Id;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? Employees : $"{Employees}?{query}"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, RecordResponse record, JsonObject values)
    {
        var response = await client.PutAsJsonAsync($"{Employees}/{record.Id}", new RecordWriteRequest(values, record.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
