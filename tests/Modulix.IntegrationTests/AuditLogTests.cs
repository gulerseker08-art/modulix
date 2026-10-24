using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Microsoft.AspNetCore.Identity;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y5a: denetim kaydı ekranının sunucu tarafı. İzin, filtreler, alan ve kayıt adları, görüntüleme izni olmayan türün
/// değerlerinin gizlenmesi, CSV dışa aktarma ve denetim kaydının değiştirilemezliği.
/// </summary>
public class AuditLogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Audit_log_needs_audit_permission_and_shows_changes_with_labels_newest_first()
    {
        var (tenant, editor, company) = await TenantWithCompanyChangeAsync();
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], outsider);
        using var outsiderClient = await LoginAsync(outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync("/api/audit/export")).StatusCode);

        using var client = await LoginAsync(editor);
        var page = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=crm.company");
        Assert.Equal(2, page!.TotalCount);
        var updated = page.Items[0];
        Assert.Equal(AuditOperation.Updated, updated.Action);
        Assert.Equal(AuditSource.Api, updated.Source);
        Assert.Equal("Firma (CRM)", updated.EntityName);
        Assert.Equal("Anka Gıda", updated.RecordTitle);
        Assert.Equal("Test Kullanıcı", updated.UserName);
        Assert.False(updated.ValuesHidden);
        var status = Assert.Single(updated.Changes, c => c.Field == "status");
        Assert.Equal("Durum", status.Label);
        Assert.Equal("Potansiyel", status.Old);
        Assert.Equal("Müşteri", status.New);
        Assert.Equal(AuditOperation.Created, page.Items[1].Action);
        Assert.Equal(company, page.Items[1].RecordId);

        // Filtreler.
        Assert.Equal(1, (await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=crm.company&action=Created"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?userId={outsider.Id}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?source=Import"))!.TotalCount);
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var tomorrow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).AddDays(1).ToString("yyyy-MM-dd");
        Assert.Equal(2, (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=crm.company&from={today}&to={today}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?from={tomorrow}"))!.TotalCount);

        var filters = await client.GetFromJsonAsync<AuditFiltersResponse>("/api/audit/filters");
        Assert.Contains(filters!.Users, u => u.UserId == editor.Id);
        Assert.Contains(filters.EntityTypes, t => t.Key == "crm.company" && t.Name == "Firma (CRM)");
    }

    [Fact]
    public async Task Values_of_types_the_reader_cannot_view_are_hidden_and_other_tenants_are_not_visible()
    {
        var (tenant, _, _) = await TenantWithCompanyChangeAsync();
        var auditor = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], auditor);
        var (other, _, otherCompany) = await TenantWithCompanyChangeAsync();
        using var client = await LoginAsync(auditor);

        var companies = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=crm.company");
        Assert.Equal(2, companies!.TotalCount);
        Assert.All(companies.Items, item =>
        {
            Assert.True(item.ValuesHidden);
            Assert.Null(item.RecordTitle);
            Assert.All(item.Changes, c => Assert.True(c.Old is null && c.New is null));
        });
        Assert.Contains(companies.Items[0].Changes, c => c.Label == "Durum");

        // Yönetim kayıtları (roller) her denetim okuruna açıktır.
        var roles = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.role");
        Assert.NotEmpty(roles!.Items);
        Assert.All(roles.Items, item => Assert.False(item.ValuesHidden));

        // Başka firmanın kaydı hiçbir filtreyle görünmez.
        var all = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?page=1");
        Assert.DoesNotContain(all!.Items, i => i.RecordId == otherCompany);
        Assert.Equal(0, (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?userId={(await OwnerOfAsync(other.Id))}"))!.TotalCount);

        var csv = await ReadCsvAsync(client, "/api/audit/export?entityKey=crm.company");
        Assert.Contains("Durum;gizli;gizli", csv);
        Assert.DoesNotContain("Anka", csv);
    }

    [Fact]
    public async Task Csv_export_is_excel_friendly_and_is_itself_audited()
    {
        var (tenant, editor, _) = await TenantWithCompanyChangeAsync();
        using var client = await LoginAsync(editor);

        var response = await client.GetAsync("/api/audit/export?entityKey=crm.company");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("denetim-kaydi-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Tarih;Kullanıcı;E-posta;Kayıt türü;Kayıt;İşlem;Kaynak;Alan;Eski değer;Yeni değer", lines[0]);
        Assert.Contains(lines, l => l.Contains(";Anka Gıda;Değiştirdi;Arayüz;Durum;Potansiyel;Müşteri"));
        Assert.Contains(lines, l => l.Contains(";Formül;") || l.Contains(";'=1+1"));

        var exports = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.audit");
        var export = Assert.Single(exports!.Items);
        Assert.Equal(AuditOperation.Exported, export.Action);
        Assert.Equal(editor.Id, export.UserId);
        Assert.Contains(export.Changes, c => c.Field == "filters" && c.New!.Contains("crm.company"));
    }

    [Fact]
    public async Task Audit_log_cannot_be_changed_or_deleted_by_the_application()
    {
        var (tenant, _, company) = await TenantWithCompanyChangeAsync();
        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE core.AuditLog SET Changes = N'{{}}' WHERE RecordId = {company}"));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM core.AuditLog WHERE RecordId = {company}"));
        Assert.Equal(2, await db.AuditLog.CountAsync(a => a.RecordId == company));
    }

    [Fact]
    public async Task Platform_administrator_appears_with_a_fixed_name()
    {
        var (tenant, editor, _) = await TenantWithCompanyChangeAsync();
        var platformAdmin = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            var stored = (await users.FindByIdAsync(platformAdmin.Id.ToString()))!;
            stored.IsPlatformAdministrator = true;
            await users.UpdateAsync(stored);
        }

        using (var scope = factory.OpenScope(tenant.Id))
        {
            scope.ServiceProvider.GetRequiredService<IAuditWriter>().Add(new AuditEntry(
                tenant.Id, platformAdmin.Id, "core.tenant", tenant.Id, AuditAction.Updated,
                RecordDiff.Compute(new() { ["status"] = "Closed" }, new() { ["status"] = "Active" }), ExecutionSource.Api, null));
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().SaveChangesAsync();
        }

        using var client = await LoginAsync(editor);
        var page = await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.tenant");
        var item = Assert.Single(page!.Items);
        Assert.Equal("Modülix platform yöneticisi", item.UserName);
        Assert.Equal("Firma bilgileri", item.EntityName);
        var status = Assert.Single(item.Changes);
        Assert.Equal(("Kapalı", "Açık"), (status.Old, status.New));
    }

    /// <summary>CRM açık firma; yönetici bir firma oluşturur ve durumunu değiştirir (iki denetim kaydı).</summary>
    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Editor, Guid Company)> TenantWithCompanyChangeAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var editor = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite, ReadAudit], editor);
        using var client = await LoginAsync(editor);

        var created = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Gıda", Notes: "=1+1"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<CompanyDto>())!;
        var updated = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}",
            new SaveCompanyRequest("Anka Gıda", Notes: "=1+1", Status: CompanyStatus.Customer, Version: company.Version));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        return (tenant, editor, company.Id);
    }

    private async Task<Guid> OwnerOfAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await db.AuditLog.AsNoTracking().Select(a => a.UserId).FirstAsync();
    }

    private static async Task<string> ReadCsvAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync())[3..]);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
