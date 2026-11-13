using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Records;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Imports;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0033 Dilim 3: firma ve kişi içe aktarma (kesin ve olası mükerrer, firma ve sorumlu bağlantısı) ile firmanın içe aktarma tasarımı
/// (tür açma, sütun seçme, zorunluluk, toplu otomasyon). Mutlu yol, doğrulama, tenant izolasyonu ve yetki.
/// </summary>
public class ImportCrmTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Company = "crm.company";
    private const string Contact = "crm.contact";
    private const string LeadKey = "crm.lead";

    private static readonly string[] Importer = [RunImports, CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, LeadsRead, LeadsCreate];

    [Fact]
    public async Task Companies_are_created_and_tax_number_is_an_exact_match_while_name_only_warns()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var first = await RunAsync(client, Company, "Firma adı;Vergi numarası;Durum;Firma türü\r\nAcme;1234567890;Müşteri;Tüzel kişi\r\n");
        Assert.Equal(("Succeeded", 1), (first.Run.Status, first.Run.CreatedCount));

        var result = await RunAsync(client, Company, "Firma adı;Vergi numarası\r\nBaşka Ad;1234567890\r\nBeta;9999999999\r\nBeta Kopya;9999999999\r\nAcme;\r\n");

        Assert.Equal(("PartiallySucceeded", 2, 2), (result.Run.Status, result.Run.CreatedCount, result.Run.FailedCount));
        Assert.Equal(
            [(2, "duplicate-exact", "Error"), (4, "duplicate-exact", "Error"), (5, "duplicate-possible", "Warning")],
            result.Issues.Items.Select(i => (i.RowNo, i.Code, i.Severity)));

        using var scope = factory.OpenScope(tenant.Id);
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Company>().CountAsync());
    }

    [Fact]
    public async Task Dry_run_finds_the_same_exact_duplicates_without_writing()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;

        var result = await RunAsync(client, Company, "Firma adı;Vergi numarası\r\nBir;1111111111\r\nİki;1111111111\r\n", preview: true);

        Assert.Equal(("DryRun", 1, 1), (result.Run.Mode, result.Run.CreatedCount, result.Run.FailedCount));
        Assert.Equal("duplicate-exact", Assert.Single(result.Issues.Items).Code);
        using var scope = factory.OpenScope(tenant.Id);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Company>().CountAsync());
    }

    [Fact]
    public async Task Contacts_link_to_a_company_by_tax_number_or_exact_name()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        await RunAsync(client, Company, "Firma adı;Vergi numarası\r\nAcme;1234567890\r\nİkiz;\r\n");
        await RunAsync(client, Company, "Firma adı\r\nİkiz\r\n");

        var result = await RunAsync(client, Contact, "Ad;Soyad;Firma\r\nAyşe;Kaya;1234567890\r\nAli;Demir;Acme\r\nCan;Ak;Olmayan\r\nDeniz;Er;İkiz\r\nEce;Su;\r\n");

        Assert.Equal((3, 2), (result.Run.CreatedCount, result.Run.FailedCount));
        Assert.Equal([(4, "lookup-unresolved"), (5, "lookup-unresolved")], result.Issues.Items.Select(i => (i.RowNo, i.Code)));
        Assert.Contains("birden çok", result.Issues.Items[1].Message);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var acme = await db.Set<Company>().AsNoTracking().Where(c => c.Party.TaxNumber == "1234567890").Select(c => c.Id).SingleAsync();
        Assert.Equal(2, await db.Set<Contact>().CountAsync(c => c.CompanyId == acme));
        Assert.Equal(2, await db.Set<CompanyContact>().CountAsync(l => l.CompanyId == acme && l.IsActive));
    }

    [Fact]
    public async Task Owner_column_accepts_active_members_only_and_a_company_outside_the_scope_is_not_found()
    {
        var tenant = await TenantAsync();
        var other = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(tenant.Id, Importer, manager);
        using var client = await LoginAsync(manager);

        var result = await RunAsync(client, Company, $"Firma adı;Sorumlu\r\nBir;{colleague.Email}\r\nİki;{stranger.Email}\r\n");

        Assert.Equal(1, result.Run.CreatedCount);
        var issue = Assert.Single(result.Issues.Items);
        Assert.Equal((3, "lookup-unresolved"), (issue.RowNo, issue.Code));
        using (var scope = factory.OpenScope(tenant.Id))
        {
            Assert.Equal(colleague.Id, (await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Company>().AsNoTracking().SingleAsync()).OwnerId);
        }

        // Yalnızca kendi kayıtlarını gören kullanıcı, başkasının firmasına bağlayamaz.
        var limited = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Importer, RecordScope.Own, limited);
        using var limitedClient = await LoginAsync(limited);
        var linked = await RunAsync(limitedClient, Contact, "Ad;Soyad;Firma\r\nAyşe;Kaya;Bir\r\n");
        Assert.Equal("lookup-unresolved", Assert.Single(linked.Issues.Items).Code);
    }

    [Fact]
    public async Task A_row_rejected_after_the_record_was_prepared_does_not_reach_the_next_save()
    {
        // Sorumlu kapsam dışıysa depo varlığı izlemeye aldıktan sonra reddedilir; paylaşılan kapsamda sonraki satırla yazılırdı.
        var tenant = await TenantAsync();
        var limited = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Importer, RecordScope.Own, limited);
        using var client = await LoginAsync(limited);

        var result = await RunAsync(client, Company, $"Firma adı;Sorumlu\r\nReddedilen;{colleague.Email}\r\nAlınan;\r\n");

        Assert.Equal((1, 1), (result.Run.CreatedCount, result.Run.FailedCount));
        using var scope = factory.OpenScope(tenant.Id);
        var names = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Company>().AsNoTracking().Select(c => c.Party.Name).ToListAsync();
        Assert.Equal("Alınan", Assert.Single(names));
    }

    [Fact]
    public async Task Design_controls_the_type_the_columns_and_the_requirements()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        using var admin = await AdminAsync(tenant);
        var design = await DesignAsync(admin, Company);
        Assert.True(design.Enabled);
        Assert.False(design.RunAutomation);
        Assert.Contains(design.Fields, f => f is { Key: "name", Locked: true, Required: true });

        var saved = await SaveAsync(admin, design, new SaveImportDesignRequest(true, false, ["notes"], ["city"], design.Version));
        Assert.Equal(1, saved.Version);

        var fields = (await client.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions"))!.Single(d => d.EntityKey == Company).Fields;
        Assert.DoesNotContain(fields, f => f.Key == "notes");
        Assert.Contains(fields, f => f is { Key: "city", Required: true });

        var missingColumn = await RunAsync(client, Company, "Firma adı\r\nBir\r\n");
        Assert.Equal(("Failed", "file-invalid"), (missingColumn.Run.Status, missingColumn.Issues.Items[0].Code));

        var emptyCell = await RunAsync(client, Company, "Firma adı;Şehir;Notlar\r\nBir;;x\r\nİki;Ankara;y\r\n");
        Assert.Equal((1, 1), (emptyCell.Run.CreatedCount, emptyCell.Run.FailedCount));
        Assert.Contains(emptyCell.Issues.Items, i => i is { Code: "required-missing", RowNo: 2 });
        Assert.Contains(emptyCell.Issues.Items, i => i is { Code: "file-invalid", Severity: "Warning" });

        await SaveAsync(admin, saved, new SaveImportDesignRequest(false, false, [], [], saved.Version));
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions"))!, d => d.EntityKey == Company);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, "/api/imports", Company, "Firma adı\r\nA\r\n")).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var audit = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.ImportDesign).ToListAsync();
        Assert.Equal([AuditAction.Created, AuditAction.Updated], audit.OrderBy(a => a.OccurredAt).Select(a => a.Action));
    }

    [Fact]
    public async Task Invalid_designs_and_stale_versions_are_rejected()
    {
        var tenant = await TenantAsync();
        using var admin = await AdminAsync(tenant);
        var design = await DesignAsync(admin, Company);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(admin, Company, new SaveImportDesignRequest(true, false, ["name"], [], 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(admin, Company, new SaveImportDesignRequest(true, false, ["yok"], [], 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, "crm.bilinmiyor", new SaveImportDesignRequest(true, false, [], [], 0))).StatusCode);

        await SaveAsync(admin, design, new SaveImportDesignRequest(true, true, [], [], 0));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(admin, Company, new SaveImportDesignRequest(true, false, [], [], 0))).StatusCode);
    }

    [Fact]
    public async Task Design_belongs_to_one_tenant_and_needs_the_customization_permission()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var other = await TenantAsync();
        using var admin = await AdminAsync(tenant);
        using var otherAdmin = await AdminAsync(other);
        await SaveAsync(admin, await DesignAsync(admin, Company), new SaveImportDesignRequest(false, true, [], [], 0));

        var seen = await DesignAsync(otherAdmin, Company);
        Assert.Equal((true, false, 0L), (seen.Enabled, seen.RunAutomation, seen.Version));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/customization/import-designs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, Company, new SaveImportDesignRequest(true, false, [], [], 1))).StatusCode);
    }

    // ADR-0033 Dilim 4e: "hepsi ya da hiç". Uygulama aynı dosyanın hatasız kuru çalışmasını ister; kuru çalışma başka firmadan sayılmaz; ayarı
    // yalnızca özelleştirme izni olan değiştirir.
    [Fact]
    public async Task All_or_nothing_needs_a_clean_rehearsal_of_the_same_file_in_the_same_firm()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        using var admin = await AdminAsync(tenant);
        var design = await DesignAsync(admin, LeadKey);
        var saved = await SaveAsync(admin, design, new SaveImportDesignRequest(true, false, [], [], design.Version, AllOrNothing: true));
        Assert.True(saved.AllOrNothing);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutAsync(client, LeadKey, new SaveImportDesignRequest(true, false, [], [], saved.Version, AllOrNothing: false))).StatusCode);

        var good = "Konu;Soyad\r\nTek işlem 1;Kaya\r\nTek işlem 2;Demir\r\n";
        var bad = "Konu;Soyad\r\nTek işlem 3;Kaya\r\n;Demir\r\n";

        // Kuru çalışmasız uygulama ve hatalı kuru çalışmadan sonra uygulama hiçbir satır almadan düşer.
        var blind = await RunAsync(client, LeadKey, good);
        Assert.Equal(("Failed", 0), (blind.Run.Status, blind.Run.CreatedCount));
        Assert.Contains(blind.Issues.Items, i => i.Code == "file-invalid" && i.Message.Contains("kuru çalışma", StringComparison.Ordinal));
        Assert.Equal("PartiallySucceeded", (await RunAsync(client, LeadKey, bad, preview: true)).Run.Status);
        Assert.Equal("Failed", (await RunAsync(client, LeadKey, bad)).Run.Status);

        // Hatasız kuru çalışmadan sonra bütün satırlar tek işlemde yazılır.
        Assert.Equal("Succeeded", (await RunAsync(client, LeadKey, good, preview: true)).Run.Status);
        var applied = await RunAsync(client, LeadKey, good);
        Assert.Equal(("Succeeded", "AllOrNothing", 2), (applied.Run.Status, applied.Run.Policy, applied.Run.CreatedCount));
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var subjects = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().Select(l => l.Subject).ToListAsync();
            Assert.Equal(["Tek işlem 1", "Tek işlem 2"], subjects.Order());
        }

        // Başka firmanın kuru çalışması bu firmanın ön koşulunu karşılamaz.
        var (otherClient, other, _) = await ImporterAsync();
        using var _o = otherClient;
        using var otherAdmin = await AdminAsync(other);
        var otherDesign = await DesignAsync(otherAdmin, LeadKey);
        await SaveAsync(otherAdmin, otherDesign, new SaveImportDesignRequest(true, false, [], [], otherDesign.Version, AllOrNothing: true));
        var stranger = "Konu;Soyad\r\nYabancı;Kaya\r\n";
        Assert.Equal("Succeeded", (await RunAsync(client, LeadKey, stranger, preview: true)).Run.Status);
        Assert.Equal("Failed", (await RunAsync(otherClient, LeadKey, stranger)).Run.Status);
    }

    // ADR-0033 Dilim 4c: kayıtlı sütun eşlemesi firmanın tasarımındadır; aynı başlıklı dosya başka firmada tanınmaz.
    [Fact]
    public async Task Saved_column_mapping_binds_the_firms_own_headers_only_in_that_firm()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        using var admin = await AdminAsync(tenant);
        var (otherClient, _, _) = await ImporterAsync();
        using var _o = otherClient;
        var file = "Müşteri talebi;Soyad\r\nEşlenen;Kaya\r\n";

        // Eşleme yokken zorunlu "Konu" sütunu bulunamaz.
        Assert.Equal("Failed", (await RunAsync(client, LeadKey, file)).Run.Status);

        // Başka sütunun adı ve iki sütuna aynı ad reddedilir; geçerli eşleme kaydedilir ve geri okunur.
        var design = await DesignAsync(admin, LeadKey);
        var taken = new SaveImportDesignRequest(true, false, [], [], design.Version, new Dictionary<string, IReadOnlyList<string>> { ["subject"] = ["Soyad"] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(admin, LeadKey, taken)).StatusCode);
        var saved = await SaveAsync(admin, design, new SaveImportDesignRequest(true, false, [], [], design.Version,
            new Dictionary<string, IReadOnlyList<string>> { ["subject"] = [" Müşteri talebi ", "müşteri TALEBİ", ""] }));
        Assert.Equal(["Müşteri talebi"], saved.Fields.Single(f => f.Key == "subject").Aliases);

        var run = await RunAsync(client, LeadKey, file);
        Assert.Equal(("Succeeded", 1), (run.Run.Status, run.Run.CreatedCount));
        Assert.DoesNotContain(run.Issues.Items, i => i.Code == "file-invalid");

        // Başka firmanın tasarımı etkilenmez.
        Assert.Equal("Failed", (await RunAsync(otherClient, LeadKey, file)).Run.Status);

        // Eşlemeyi yalnızca özelleştirme izni olan kaydeder.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, LeadKey, new SaveImportDesignRequest(true, false, [], [], saved.Version,
            new Dictionary<string, IReadOnlyList<string>> { ["subject"] = ["Başka"] }))).StatusCode);
    }

    [Fact]
    public async Task Imported_records_stay_silent_unless_the_design_opens_automation()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        using var admin = await AdminAsync(tenant);

        await RunAsync(client, LeadKey, "Konu;Soyad\r\nSessiz;Kaya\r\n");
        await SaveAsync(admin, await DesignAsync(admin, LeadKey), new SaveImportDesignRequest(true, true, [], [], 0));
        await RunAsync(client, LeadKey, "Konu;Soyad\r\nKurallı;Kaya\r\n");

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var leads = await db.Set<Lead>().AsNoTracking().ToDictionaryAsync(l => l.Subject, l => l.Id);
        var events = await db.OutboxMessages.AsNoTracking().Where(m => m.EntityKey == LeadKey).ToListAsync();
        Assert.Equal(EventReasons.Import, events.Single(e => e.RecordId == leads["Sessiz"]).Reason);
        Assert.Null(events.Single(e => e.RecordId == leads["Kurallı"]).Reason);
    }

    private static async Task<ImportDesignDto> DesignAsync(HttpClient client, string entityKey) =>
        (await client.GetFromJsonAsync<List<ImportDesignDto>>("/api/customization/import-designs"))!.Single(d => d.EntityKey == entityKey);

    private static async Task<ImportDesignDto> SaveAsync(HttpClient client, ImportDesignDto design, SaveImportDesignRequest request)
    {
        var response = await PutAsync(client, design.EntityKey, request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ImportDesignDto>())!;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string entityKey, SaveImportDesignRequest request) =>
        client.PutAsJsonAsync($"/api/customization/import-designs/{entityKey}", request);

    private async Task<HttpClient> AdminAsync(Tenant tenant)
    {
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return await LoginAsync(admin);
    }

    private async Task<Tenant> TenantAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", true);
        return tenant;
    }

    private async Task<(HttpClient Client, Tenant Tenant, ModulixUser User)> ImporterAsync()
    {
        var tenant = await TenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Importer, user);
        return (await LoginAsync(user), tenant, user);
    }

    private static async Task<ImportRunDetailDto> RunAsync(HttpClient client, string entityKey, string csv, bool preview = false)
    {
        var response = await PostAsync(client, preview ? "/api/imports/preview" : "/api/imports", entityKey, csv);
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
