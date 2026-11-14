using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Imports;
using Modulix.Shared.Records;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 bölüm 19: İK içe aktarma (ADR-0033). Mutlu yol (demirbaş, izin hakkı açılış bakiyeleri sicil numarasıyla, resmî tatil), doğrulama (mevcut ve
/// dosya içi envanter no tekrarı, küsuratlı yıl, bilinmeyen sicil no), tenant izolasyonu (başka firmanın sicil numarası çözülmez) ve yetki (demirbaş
/// yönetimi izni olmayan demirbaş içe aktaramaz).
/// </summary>
public class HrImportTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Assets = "hr.asset";
    private const string Balances = "hr.leave-balance";
    private const string Holidays = "hr.holiday";

    private static readonly string[] Importer = [RunImports, EmployeesRead, AssetsRead, AssetsManage, LeavesRead, LeavesManage];

    [Fact]
    public async Task Assets_leave_balances_and_holidays_are_imported_and_balances_link_to_employees_by_number()
    {
        var s = await SetUpAsync();
        using var admin = await AdminAsync(s.Tenant);
        await EmployeeAsync(admin, "s-101");

        var assets = await RunAsync(s.Client, Assets, "Envanter no;Ad;Kategori;Alış tarihi;Alış bedeli;Para birimi\r\n"
            + "dz-1;İş bilgisayarı;Dizüstü bilgisayar;15.03.2026;45.000,50;TRY\r\nmn-1;Ekran;Monitor;;;\r\n");
        Assert.Equal(("Succeeded", 2, 0), (assets.Run.Status, assets.Run.CreatedCount, assets.Run.FailedCount));
        var laptop = Assert.Single((await s.Client.GetFromJsonAsync<RecordPageResponse>("/api/records/hr.asset?pageSize=50"))!.Items, a => Text(a, "assetTag") == "DZ-1");
        Assert.Equal(("DZ-1", "Laptop", "inStock", 45000.50m),
            (Text(laptop, "assetTag"), Text(laptop, "categoryKey"), Text(laptop, "status"), laptop.Values["purchaseCost"]!.GetValue<decimal>()));

        var balances = await RunAsync(s.Client, Balances, "Sicil no;Yıl;Hak (gün);Devreden (gün)\r\nS-101;2026;14;3,5\r\n");
        Assert.Equal(("Succeeded", 1), (balances.Run.Status, balances.Run.CreatedCount));
        var balance = (await s.Client.GetFromJsonAsync<RecordPageResponse>("/api/records/hr.leave-balance?year=2026"))!.Items.Single();
        Assert.Equal((14m, 3.5m, 17.5m),
            (balance.Values["entitledDays"]!.GetValue<decimal>(), balance.Values["carriedOverDays"]!.GetValue<decimal>(), balance.Values["remainingDays"]!.GetValue<decimal>()));

        var holidays = await RunAsync(s.Client, Holidays, "Tarih;Ad;Yarım gün\r\n29.10.2026;Cumhuriyet Bayramı;Hayır\r\n28.10.2026;Cumhuriyet Bayramı arifesi;Evet\r\n");
        Assert.Equal(("Succeeded", 2), (holidays.Run.Status, holidays.Run.CreatedCount));
    }

    [Fact]
    public async Task Duplicate_tags_fractional_years_and_unknown_employee_numbers_are_row_errors()
    {
        var s = await SetUpAsync();
        using var admin = await AdminAsync(s.Tenant);
        await EmployeeAsync(admin, "s-201");
        await RunAsync(s.Client, Assets, "Envanter no;Ad;Kategori\r\nA-1;Mevcut;Monitor\r\n");

        // Kayıtlı envanter no ve dosya içinde tekrar eden envanter no satır hatasıdır; diğer satır yüklenir.
        var assets = await RunAsync(s.Client, Assets, "Envanter no;Ad;Kategori\r\na-1;Tekrar;Monitor\r\nB-1;Yeni;Monitor\r\nB-1;Yine;Monitor\r\n");
        Assert.Equal((1, 2), (assets.Run.CreatedCount, assets.Run.FailedCount));
        Assert.Contains(assets.Issues.Items, i => i.RowNo == 2 && i.Severity == "Error");
        Assert.Contains(assets.Issues.Items, i => i.RowNo == 4 && i.Severity == "Error");

        var balances = await RunAsync(s.Client, Balances, "Sicil no;Yıl;Hak (gün)\r\nS-201;2026,5;14\r\nYOK-1;2026;14\r\nS-201;2026;14\r\n");
        Assert.Equal((1, 2), (balances.Run.CreatedCount, balances.Run.FailedCount));
        Assert.Contains(balances.Issues.Items, i => i.RowNo == 2 && i.ColumnKey == "year");
        Assert.Contains(balances.Issues.Items, i => i is { RowNo: 3, ColumnKey: "employeeId", Code: "lookup-unresolved" });
    }

    [Fact]
    public async Task Employee_numbers_of_another_tenant_do_not_resolve_and_importing_needs_the_module_permission()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminB = await AdminAsync(b.Tenant);
        await EmployeeAsync(adminB, "b-1");

        var crossTenant = await RunAsync(a.Client, Balances, "Sicil no;Yıl;Hak (gün)\r\nB-1;2026;14\r\n");
        Assert.Equal((0, 1), (crossTenant.Run.CreatedCount, crossTenant.Run.FailedCount));
        Assert.Contains(crossTenant.Issues.Items, i => i is { ColumnKey: "employeeId", Code: "lookup-unresolved" });

        // Demirbaş yönetimi izni olmayan içe aktarıcı demirbaş türünü görmez ve yükleyemez.
        var clerk = await factory.CreateUserAsync(a.Tenant);
        await factory.CreateRoleAsync(a.Tenant.Id, [RunImports, LeavesRead, LeavesManage], Scope.All, clerk);
        using var clerkClient = await LoginAsync(clerk);
        var definitions = (await clerkClient.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions"))!;
        Assert.DoesNotContain(definitions, d => d.EntityKey == Assets);
        Assert.Contains(definitions, d => d.EntityKey == Balances);
        Assert.NotEqual(HttpStatusCode.Accepted, (await PostAsync(clerkClient, "/api/imports", Assets, "Envanter no;Ad\r\nX;Y\r\n")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Tenant Tenant, HttpClient Client);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Importer, Scope.All, user);
        return new Setup(tenant, await LoginAsync(user));
    }

    private async Task<HttpClient> AdminAsync(Tenant tenant)
    {
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return await LoginAsync(admin);
    }

    private static async Task EmployeeAsync(HttpClient client, string number)
    {
        var person = await PostAsync(client, "/api/records/core.person", new JsonObject { ["firstName"] = "Sicil", ["lastName"] = number });
        await PostAsync(client, "/api/records/hr.employee", new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["employeeNumber"] = number,
        });
    }

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ImportRunDetailDto> RunAsync(HttpClient client, string entityKey, string csv)
    {
        var response = await PostAsync(client, "/api/imports", entityKey, csv);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<ImportRunDetailDto>())!;
        for (var attempt = 0; detail.Run.Status is "Pending" or "Running"; attempt++)
        {
            Assert.True(attempt < 1200, "İçe aktarma bitmedi.");
            await Task.Delay(100);
            detail = (await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{detail.Run.Id}?pageSize=200"))!;
        }

        return (await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{detail.Run.Id}?pageSize=200"))!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string entityKey, string csv)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return client.PostAsync($"{url}?entityKey={entityKey}&fileName=liste.csv", content);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
