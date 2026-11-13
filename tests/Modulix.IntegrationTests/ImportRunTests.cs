using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Imports;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Imports;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Core.Platform;
using Modulix.Modules.Crm.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Customization;
using Modulix.Shared.Imports;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0033 Dilim 2: içe aktarma motoru. Mutlu yol (kuru çalışma yazmaz, uygulama boru hattından yazar), doğrulama (satır ve dosya
/// hataları, hata raporu), tenant izolasyonu ve yetki (izin, oluşturma izni, modül ve lisans, raporun yalnızca yükleyene verilmesi).
/// </summary>
public class ImportRunTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Lead = "crm.lead";

    private static readonly string[] Importer = [RunImports, LeadsRead, LeadsCreate];

    [Fact]
    public async Task Dry_run_reports_what_would_happen_and_writes_no_record()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;

        var result = await PreviewAsync(client, "Konu;Soyad;E-posta\r\nFilo;Yılmaz;a@acme.test\r\nDepo;Kaya;gecersiz\r\n");

        Assert.Equal(("DryRun", "PartiallySucceeded", 2, 1, 1), (result.Run.Mode, result.Run.Status, result.Run.TotalRows, result.Run.CreatedCount, result.Run.FailedCount));
        var issue = Assert.Single(result.Issues.Items);
        Assert.Equal((3, "email", "format-invalid"), (issue.RowNo, issue.ColumnKey, issue.Code));

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(0, await db.Set<Lead>().CountAsync());
        Assert.Equal(0, await db.AuditLog.CountAsync(a => a.EntityKey == Lead));
    }

    [Fact]
    public async Task Apply_creates_valid_rows_through_the_pipeline_and_skips_invalid_ones()
    {
        var (client, tenant, user) = await ImporterAsync();
        using var _c = client;

        var result = await ApplyAsync(client, "Konu;Soyad;Öncelik;Tahmini bütçe;Para birimi;Hedef satın alma tarihi\r\n"
            + "Filo;Yılmaz;Yüksek;1.250,50;TRY;31.12.2026\r\n"
            + ";Kaya;;;;\r\n"
            + "Depo;Demir;Çok acil;;;\r\n"
            + "Kimliksiz;;;;;\r\n");

        Assert.Equal(("Apply", "PartiallySucceeded", 4, 1, 3), (result.Run.Mode, result.Run.Status, result.Run.TotalRows, result.Run.CreatedCount, result.Run.FailedCount));
        Assert.Equal(
            [(3, "required-missing"), (4, "lookup-unresolved"), (5, "required-missing")],
            result.Issues.Items.Select(i => (i.RowNo, i.Code)));

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var lead = await db.Set<Lead>().AsNoTracking().SingleAsync();
        Assert.Equal(("Filo", LeadPriority.High, 1250.50m, user.Id), (lead.Subject, lead.Priority, lead.EstimatedBudget, lead.OwnerId));
        Assert.Equal(new DateOnly(2026, 12, 31), lead.TargetPurchaseDate);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.RecordId == lead.Id);
        Assert.Equal(ExecutionSource.Import, audit.Source);
        Assert.Equal((await db.ImportRuns.AsNoTracking().SingleAsync()).CorrelationId, audit.CorrelationId);
    }

    [Fact]
    public async Task A_rejected_row_does_not_leak_into_the_next_row()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;

        var result = await ApplyAsync(client, "Konu;Soyad;Kaynak;Web sitesi\r\nBir;Kaya;;ftp://yanlis\r\nİki;Demir;;\r\n");

        Assert.Equal(1, result.Run.CreatedCount);
        using var scope = factory.OpenScope(tenant.Id);
        Assert.Equal("İki", (await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().SingleAsync()).Subject);
    }

    [Fact]
    public async Task Possible_duplicates_warn_but_do_not_block()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        await ApplyAsync(client, "Konu;E-posta\r\nİlk;kayit@acme.test\r\n");

        var result = await ApplyAsync(client, "Konu;E-posta\r\nİkinci;kayit@acme.test\r\nÜçüncü;yeni@acme.test\r\nDördüncü;yeni@acme.test\r\n");

        Assert.Equal(("Succeeded", 3, 0), (result.Run.Status, result.Run.CreatedCount, result.Run.FailedCount));
        Assert.Equal([2, 4], result.Issues.Items.Select(i => i.RowNo));
        Assert.All(result.Issues.Items, i => Assert.Equal(("duplicate-possible", "Warning"), (i.Code, i.Severity)));
        using var scope = factory.OpenScope(tenant.Id);
        Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());
    }

    [Fact]
    public async Task Error_report_returns_failed_rows_and_can_be_fixed_and_reuploaded()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var result = await ApplyAsync(client, "Konu;Soyad;Telefon\r\nİyi;Kaya;\r\n;Demir;+905321112233\r\n");

        var report = await client.GetAsync($"/api/imports/{result.Run.Id}/error-report");

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var text = Encoding.UTF8.GetString(await report.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Konu;Soyad;Telefon;Hata", lines[0]);
        Assert.StartsWith(";Demir;'+905321112233;", lines[1]);
        Assert.Equal(2, lines.Length);

        var fixedFile = text.Replace(";Demir;", "Düzeltildi;Demir;", StringComparison.Ordinal);
        var second = await ApplyAsync(client, fixedFile);

        Assert.Equal(("Succeeded", 1), (second.Run.Status, second.Run.CreatedCount));
        Assert.Empty(second.Issues.Items);
        using var scope = factory.OpenScope(tenant.Id);
        var fixedLead = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().SingleAsync(l => l.Subject == "Düzeltildi");
        Assert.Equal("+905321112233", fixedLead.NormalizedPhone);
    }

    [Fact]
    public async Task File_level_problems_fail_the_run_without_touching_rows()
    {
        var (client, _, _) = await ImporterAsync();
        using var _c = client;

        var missing = await ApplyAsync(client, "Soyad\r\nKaya\r\n");
        Assert.Equal(("Failed", "file-invalid", 0), (missing.Run.Status, missing.Issues.Items[0].Code, missing.Issues.Items[0].RowNo));

        var headerOnly = await ApplyAsync(client, "Konu;Soyad\r\n");
        Assert.Equal("Failed", headerOnly.Run.Status);

        var tooMany = new StringBuilder("Konu;Soyad\r\n");
        for (var i = 0; i <= ImportLimits.MaxRows; i++)
        {
            tooMany.Append("a;b\r\n");
        }

        var large = await ApplyAsync(client, tooMany.ToString());
        Assert.Equal(("Failed", 0), (large.Run.Status, large.Run.CreatedCount));

        var unknown = await ApplyAsync(client, "Konu;Soyad;Bizim kod\r\nFilo;Kaya;X1\r\n");
        Assert.Equal(("Succeeded", 1), (unknown.Run.Status, unknown.Run.CreatedCount));
        Assert.Equal(("file-invalid", "Warning"), (unknown.Issues.Items[0].Code, unknown.Issues.Items[0].Severity));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/imports", Lead, "a.xls", "Konu\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, "/api/imports", "crm.bilinmiyor", "a.csv", "Konu\r\n")).StatusCode);
        Assert.Equal(ImportLimits.MaxFileBytes, ImportFileLimits.MaxFileBytes);
    }

    [Fact]
    public async Task Definitions_and_template_include_custom_columns_of_the_tenant_only()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        await AddCustomFieldAsync(tenant, "Araç tipi");
        var (other, _, _) = await ImporterAsync();
        using var _o = other;

        var definition = Assert.Single(await client.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions") ?? []);
        Assert.Equal((Lead, "Müşteri adayı"), (definition.EntityKey, definition.DisplayName));
        Assert.Contains(definition.Fields, f => f is { Key: "subject", Required: true });
        Assert.Contains(definition.Fields, f => f.Key == "custom.arac_tipi");
        Assert.DoesNotContain(
            Assert.Single(await other.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions") ?? []).Fields,
            f => f.Key == "custom.arac_tipi");

        var template = await client.GetStringAsync($"/api/imports/definitions/{Lead}/template");
        Assert.StartsWith("Konu;Ad;Soyad;", template.TrimStart('﻿'));
        Assert.Contains("Araç tipi", template);

        var result = await ApplyAsync(client, "Konu;Soyad;Araç tipi\r\nFilo;Kaya;Tır\r\n");
        Assert.Equal(1, result.Run.CreatedCount);
        using var scope = factory.OpenScope(tenant.Id);
        var stored = (await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().SingleAsync()).CustomFields;
        Assert.Equal("Tır", System.Text.Json.Nodes.JsonNode.Parse(stored!)!["arac_tipi"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_tenant_cannot_reach_the_run_or_the_report_of_another()
    {
        var (clientA, tenantA, _) = await ImporterAsync();
        var (clientB, tenantB, _) = await ImporterAsync();
        using var _a = clientA;
        using var _b = clientB;

        var result = await ApplyAsync(clientA, "Konu;Soyad\r\nA;Kaya\r\n;Demir\r\n");

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/imports/{result.Run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/imports/{result.Run.Id}/error-report")).StatusCode);
        using var scope = factory.OpenScope(tenantB.Id);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());
        using var own = factory.OpenScope(tenantA.Id);
        Assert.Equal(1, await own.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());
    }

    [Fact]
    public async Task Starting_needs_the_run_and_the_create_permission()
    {
        var tenant = await TenantAsync();
        var reader = await factory.CreateUserAsync(tenant);
        var noCreate = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports, LeadsCreate], reader);
        await factory.CreateRoleAsync(tenant.Id, [RunImports, LeadsRead], noCreate);
        using var readerClient = await LoginAsync(reader);
        using var noCreateClient = await LoginAsync(noCreate);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(readerClient, "/api/imports", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(readerClient, "/api/imports/preview", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/imports/definitions")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(noCreateClient, "/api/imports", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);
        Assert.Empty(await noCreateClient.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions") ?? []);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/api/imports", Lead, "a.csv", "Konu\r\n")).StatusCode);
    }

    [Fact]
    public async Task Error_report_is_given_only_to_the_uploader()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], manager);
        using var managerClient = await LoginAsync(manager);
        var result = await ApplyAsync(client, "Konu;Soyad\r\n;Demir\r\n");

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/api/imports/{result.Run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerClient.GetAsync($"/api/imports/{result.Run.Id}/error-report")).StatusCode);
    }

    [Fact]
    public async Task Closed_or_read_only_module_is_not_importable()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;

        await factory.SetEntitlementEndAsync(tenant.Id, "crm", DateTimeOffset.UtcNow.AddYears(-1));
        Assert.Empty(await client.GetFromJsonAsync<List<ImportDefinitionDto>>("/api/imports/definitions") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, "/api/imports", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);

        await factory.SetEntitlementEndAsync(tenant.Id, "crm", null);
        await factory.SetModuleAsync(tenant.Id, "crm", false);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, "/api/imports/preview", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);
    }

    [Fact]
    public async Task Only_one_run_per_tenant_and_the_lock_is_released_when_it_ends()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var (otherClient, _, _) = await ImporterAsync();
        using var _o = otherClient;
        var connectionString = new SqlConnectionStringBuilder(factory.Services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString) { Pooling = false }.ConnectionString;

        // Havuzsuz ayrı oturum: kilit başka koşuda gibi tutulur.
        await using (var held = new SqlConnection(connectionString))
        {
            await held.OpenAsync();
            Assert.True(await TryLockAsync(held, tenant.Id));

            Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "/api/imports", Lead, "a.csv", "Konu;Soyad\r\nA;B\r\n")).StatusCode);
            Assert.Equal(1, (await ApplyAsync(otherClient, "Konu;Soyad\r\nA;B\r\n")).Run.CreatedCount);
        }

        await ApplyAsync(client, "Konu;Soyad\r\nA;B\r\n");

        // Koşu bitince kilit başka oturumdan alınabilmeli (havuza dönen bağlantıda kalmamalı).
        await using var after = new SqlConnection(connectionString);
        await after.OpenAsync();
        Assert.True(await TryLockAsync(after, tenant.Id));
    }

    private static async Task<bool> TryLockAsync(SqlConnection connection, Guid tenantId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 5000; SELECT @r;";
        command.Parameters.AddWithValue("@name", ImportRunner.LockResource(tenantId));
        return Convert.ToInt32(await command.ExecuteScalarAsync()) >= 0;
    }

    [Fact]
    public async Task A_run_left_running_by_a_crash_is_closed_by_the_next_run()
    {
        var (client, tenant, user) = await ImporterAsync();
        using var _c = client;
        var dead = ImportRun.Create(tenant.Id, user.Id, Lead, "yarim.csv", 10, new string('a', ImportRun.FileHashLength), 1,
            ImportRunMode.Apply, ImportFailurePolicy.SkipInvalidRows, null, DateTimeOffset.UtcNow);
        dead.Start(5, DateTimeOffset.UtcNow);
        using (var seed = factory.OpenScope(tenant.Id))
        {
            var db = seed.ServiceProvider.GetRequiredService<ModulixDbContext>();
            db.ImportRuns.Add(dead);
            await db.SaveChangesAsync();
        }

        await ApplyAsync(client, "Konu;Soyad\r\nA;B\r\n");

        var detail = await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{dead.Id}");
        Assert.Equal("Failed", detail!.Run.Status);
    }

    // ADR-0033 Dilim 4b: aynı dosya uyarısı. Uyarıdır (yükleme sürer), yalnızca kayıt almış uygulamaya bakar, firma sınırını aşmaz ve önceki yükleyeni söylemez.
    [Fact]
    public async Task Reuploading_an_applied_file_warns_without_blocking_and_only_within_the_tenant()
    {
        var (client, tenant, user) = await ImporterAsync();
        using var _c = client;
        var file = "Konu;Soyad\r\nTekrar;Kaya\r\n";

        // Önceki uygulama yokken uyarı yok; kayıt almayan (bütün satırları hatalı) uygulama da sayılmaz.
        Assert.DoesNotContain((await PreviewAsync(client, file)).Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);
        Assert.Equal("Failed", (await ApplyAsync(client, "Konu;Soyad\r\n;Kaya\r\n")).Run.Status);
        Assert.DoesNotContain((await PreviewAsync(client, "Konu;Soyad\r\n;Kaya\r\n")).Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);

        var first = await ApplyAsync(client, file);
        Assert.Equal(("Succeeded", 1), (first.Run.Status, first.Run.CreatedCount));
        Assert.DoesNotContain(first.Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);

        // Kuru çalışma ve ikinci uygulama uyarır; uyarı yüklemeyi durdurmaz.
        var preview = await PreviewAsync(client, file);
        var warning = Assert.Single(preview.Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);
        Assert.Equal((0, "Warning"), (warning.RowNo, warning.Severity));
        var second = await ApplyAsync(client, file);
        Assert.Equal(("Succeeded", 1), (second.Run.Status, second.Run.CreatedCount));
        Assert.Contains(second.Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);

        // Aynı firmadaki başka yükleyen de uyarıyı görür ama ileti önceki yükleyenin adını taşımaz.
        var colleague = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Importer, colleague);
        using var colleagueClient = await LoginAsync(colleague);
        var seen = Assert.Single((await PreviewAsync(colleagueClient, file)).Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);
        Assert.DoesNotContain(user.Email!, seen.Message, StringComparison.OrdinalIgnoreCase);

        // Başka firma aynı dosyayı ilk kez yüklüyor sayılır.
        var (other, _, _) = await ImporterAsync();
        using var _o = other;
        Assert.DoesNotContain((await ApplyAsync(other, file)).Issues.Items, i => i.Code == ImportIssueCodes.FileRepeated);
    }

    // ADR-0033 Dilim 4d: geri alma. Dokunulmamış kayıtlar silinir, sonradan değişen korunur; gerekçe zorunlu; tek sefer; firma sınırı; izin.
    [Fact]
    public async Task Rollback_removes_untouched_imported_records_keeps_changed_ones_and_needs_its_permission()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;
        var applied = await ApplyAsync(client, "Konu;Soyad\r\nBir;Kaya\r\nİki;Demir\r\nÜç;Ak\r\n");
        Assert.Equal(3, applied.Run.CreatedCount);
        var rollbackUrl = $"/api/imports/{applied.Run.Id}/rollback";

        // İçe aktarma izni geri alma izni değildir.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(rollbackUrl, new ImportRollbackRequest("Yanlış dosya"))).StatusCode);

        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [RunImports, ReadImports, RollbackImports, LeadsRead, LeadsUpdate, LeadsDelete], manager);
        using var managerClient = await LoginAsync(manager);

        // Bir kayıt içe aktarmadan sonra değiştirilir: geri almada korunmalı.
        Guid changedId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            changedId = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().Where(l => l.Subject == "İki").Select(l => l.Id).SingleAsync();
        }

        var current = (await managerClient.GetFromJsonAsync<Modulix.Shared.Records.RecordResponse>($"/api/records/{Lead}/{changedId}"))!;
        var update = await managerClient.PutAsJsonAsync($"/api/records/{Lead}/{changedId}",
            new Modulix.Shared.Records.RecordWriteRequest(new System.Text.Json.Nodes.JsonObject { ["subject"] = "İki (düzeltildi)" }, current.Version));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        // Gerekçe zorunlu; başka firmanın yöneticisi bu koşuyu bulamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerClient.PostAsJsonAsync(rollbackUrl, new ImportRollbackRequest(" "))).StatusCode);
        var (otherClient, otherTenant, _) = await ImporterAsync();
        otherClient.Dispose();
        var otherManager = await factory.CreateUserAsync(otherTenant);
        await factory.CreateRoleAsync(otherTenant.Id, [ReadImports, RollbackImports, LeadsRead, LeadsDelete], otherManager);
        using (var otherManagerClient = await LoginAsync(otherManager))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await otherManagerClient.PostAsJsonAsync(rollbackUrl, new ImportRollbackRequest("Deneme"))).StatusCode);
        }

        var response = await managerClient.PostAsJsonAsync(rollbackUrl, new ImportRollbackRequest("Yanlış dosya yüklendi"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var outcome = (await response.Content.ReadFromJsonAsync<ImportRollbackDto>())!;
        Assert.Equal((2, 0, "RolledBack"), (outcome.Removed, outcome.AlreadyRemoved, outcome.Run.Status));
        Assert.Equal(changedId, Assert.Single(outcome.Kept).RecordId);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var subjects = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().Select(l => l.Subject).ToListAsync();
            Assert.Equal(["İki (düzeltildi)"], subjects);
        }

        // İkinci geri alma ve kuru çalışmanın geri alınması reddedilir.
        Assert.Equal(HttpStatusCode.Conflict, (await managerClient.PostAsJsonAsync(rollbackUrl, new ImportRollbackRequest("Tekrar"))).StatusCode);
        var preview = await PreviewAsync(client, "Konu;Soyad\r\nKuru;Kaya\r\n");
        Assert.Equal(HttpStatusCode.Conflict,
            (await managerClient.PostAsJsonAsync($"/api/imports/{preview.Run.Id}/rollback", new ImportRollbackRequest("Kuru"))).StatusCode);
    }

    private async Task AddCustomFieldAsync(Tenant tenant, string label)
    {
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);
        var response = await client.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(Lead, label, CustomFieldType.Text));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    // ADR-0033 Dilim 4a: .xlsx çalışma kitabı CSV ile aynı yoldan (boru hattı, günlük, yetki) geçer.
    [Fact]
    public async Task Xlsx_workbook_runs_through_the_same_pipeline_with_typed_cells()
    {
        var (client, tenant, user) = await ImporterAsync();
        using var _c = client;

        // Sayı ve tarih hücreleri gerçek tiptedir (Excel'in sakladığı gibi); boş satır numarayı ilerletir.
        var workbook = Workbook(
            new Dictionary<string, object?> { ["Konu"] = "Filo", ["Soyad"] = "Yılmaz", ["Tahmini bütçe"] = 1250.5, ["Hedef satın alma tarihi"] = new DateTime(2026, 12, 31), ["E-posta"] = "a@acme.test" },
            new Dictionary<string, object?> { ["Konu"] = null, ["Soyad"] = null, ["Tahmini bütçe"] = null, ["Hedef satın alma tarihi"] = null, ["E-posta"] = null },
            new Dictionary<string, object?> { ["Konu"] = "Depo", ["Soyad"] = "Kaya", ["Tahmini bütçe"] = 100d, ["Hedef satın alma tarihi"] = null, ["E-posta"] = "gecersiz" });

        var result = await RunBytesAsync(client, "/api/imports", "liste.xlsx", workbook);

        Assert.Equal(("Apply", "PartiallySucceeded", 2, 1, 1), (result.Run.Mode, result.Run.Status, result.Run.TotalRows, result.Run.CreatedCount, result.Run.FailedCount));
        var issue = Assert.Single(result.Issues.Items);
        Assert.Equal((4, "email", "format-invalid"), (issue.RowNo, issue.ColumnKey, issue.Code));

        using var scope = factory.OpenScope(tenant.Id);
        var lead = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().AsNoTracking().SingleAsync();
        Assert.Equal(("Filo", 1250.50m, user.Id), (lead.Subject, lead.EstimatedBudget, lead.OwnerId));
        Assert.Equal(new DateOnly(2026, 12, 31), lead.TargetPurchaseDate);
    }

    [Fact]
    public async Task Broken_or_unsupported_workbooks_fail_the_run_without_writing_records()
    {
        var (client, tenant, _) = await ImporterAsync();
        using var _c = client;

        // Uzantısı .xlsx olan düz metin ve zip imzalı ama çalışma kitabı olmayan içerik: dosya sorunu olarak günlüğe yazılır.
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("Konu;Soyad\r\nA;B\r\n"), new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0 } })
        {
            var result = await RunBytesAsync(client, "/api/imports", "bozuk.xlsx", bytes);
            Assert.Equal(("Failed", 0), (result.Run.Status, result.Run.CreatedCount));
            Assert.Contains(result.Issues.Items, i => i is { Code: "file-invalid", Severity: "Error" });
        }

        using var scope = factory.OpenScope(tenant.Id);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());

        // Yetki ve oturum CSV ile aynı uçta denetlenir.
        using var anonymous = factory.CreateBrowserClient();
        var response = await anonymous.PostAsync($"/api/imports?entityKey={Lead}&fileName=a.xlsx", new ByteArrayContent(Workbook(new Dictionary<string, object?> { ["Konu"] = "X" })));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Xlsx_upload_is_isolated_between_tenants()
    {
        var (clientA, tenantA, _) = await ImporterAsync();
        var (clientB, tenantB, _) = await ImporterAsync();
        using var _a = clientA;
        using var _b = clientB;

        await RunBytesAsync(clientA, "/api/imports", "a.xlsx", Workbook(new Dictionary<string, object?> { ["Konu"] = "Yalnızca A", ["Soyad"] = "Kaya" }));

        using var scopeA = factory.OpenScope(tenantA.Id);
        Assert.Equal(1, await scopeA.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());
        using var scopeB = factory.OpenScope(tenantB.Id);
        Assert.Equal(0, await scopeB.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Lead>().CountAsync());
        var logB = await clientB.GetFromJsonAsync<ImportRunPageDto>("/api/imports");
        Assert.Empty(logB!.Items);
    }

    private static byte[] Workbook(params Dictionary<string, object?>[] rows)
    {
        using var stream = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(stream, rows);
        return stream.ToArray();
    }

    private static async Task<ImportRunDetailDto> RunBytesAsync(HttpClient client, string url, string fileName, byte[] bytes)
    {
        var response = await client.PostAsync($"{url}?entityKey={Lead}&fileName={fileName}", new ByteArrayContent(bytes));
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<ImportRunDetailDto>())!;
        for (var attempt = 0; detail.Run.Status is "Pending" or "Running"; attempt++)
        {
            Assert.True(attempt < 1200, "İçe aktarma bitmedi.");
            await Task.Delay(100);
            detail = (await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{detail.Run.Id}?pageSize=200"))!;
        }

        return detail;
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

    private static Task<ImportRunDetailDto> PreviewAsync(HttpClient client, string csv) => RunAsync(client, "/api/imports/preview", csv);

    private static Task<ImportRunDetailDto> ApplyAsync(HttpClient client, string csv) => RunAsync(client, "/api/imports", csv);

    private static async Task<ImportRunDetailDto> RunAsync(HttpClient client, string url, string csv)
    {
        var response = await PostAsync(client, url, Lead, "liste.csv", csv);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var detail = (await response.Content.ReadFromJsonAsync<ImportRunDetailDto>())!;

        // Satırlar arka planda işlenir; koşu bitene kadar izlenir.
        for (var attempt = 0; detail.Run.Status is "Pending" or "Running"; attempt++)
        {
            Assert.True(attempt < 1200, "İçe aktarma bitmedi.");
            await Task.Delay(100);
            detail = (await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{detail.Run.Id}?pageSize=200"))!;
        }

        return detail;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string entityKey, string fileName, string csv)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return client.PostAsync($"{url}?entityKey={entityKey}&fileName={fileName}", content);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
