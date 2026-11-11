using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Tenancy;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Files;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0040: kayda bağlı dosyalar. Mutlu yol (yükle, listele, indir, sil, denetim), doğrulama (boyut, tür, imza, yinelenen içerik), tenant izolasyonu
/// (başka tenant görmez, indirmez, silmez; dışa aktarma ve silme içeriği kapsar) ve yetki (kaydı göremeyen 404, yazma izni olmayan 403, oturumsuz 401).
/// </summary>
public sealed class FileTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/files";
    private const string Entity = "crm.company";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");

    private static readonly string[] Everything = [CompaniesRead, CompaniesWrite];

    private async Task<(Tenant Tenant, ModulixUser User, HttpClient Client, CompanyDto Company)> StartAsync(string[]? permissions = null)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions ?? Everything, RecordScope.All, user);
        var client = await LoginAsync(user.Email!);
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Dosya Ltd. {Guid.NewGuid():N}"[..24]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (tenant, user, client, (await response.Content.ReadFromJsonAsync<CompanyDto>())!);
    }

    private async Task<(ModulixUser User, HttpClient Client)> AddUserAsync(Tenant tenant, string[] permissions, RecordScope scope = RecordScope.All)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, scope, user);
        return (user, await LoginAsync(user.Email!));
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password))).StatusCode);
        return client;
    }

    private static MultipartFormDataContent Form(Guid recordId, string fileName, byte[] content, string entityKey = Entity)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent(entityKey), "entityKey" },
            { new StringContent(recordId.ToString()), "recordId" },
            { file, "file", fileName },
        };
    }

    private static async Task<StoredFileDto> UploadAsync(HttpClient client, Guid recordId, string fileName, byte[] content)
    {
        using var form = Form(recordId, fileName, content);
        var response = await client.PostAsync(Url, form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
    }

    private static Task<List<StoredFileDto>> ListAsync(HttpClient client, Guid recordId, string entityKey = Entity) =>
        client.GetFromJsonAsync<List<StoredFileDto>>($"{Url}?entityKey={entityKey}&recordId={recordId}")!;

    private static byte[] Distinct(int seed) => [.. Pdf, .. Encoding.ASCII.GetBytes($"içerik-{seed}")];

    // ---- 1. Mutlu yol

    [Fact]
    public async Task A_file_is_uploaded_listed_downloaded_and_deleted_with_audit()
    {
        var (tenant, _, client, company) = await StartAsync();

        var file = await UploadAsync(client, company.Id, "Sözleşme taslağı.pdf", Pdf);
        Assert.Equal(("Sözleşme taslağı.pdf", "application/pdf", (long)Pdf.Length, "NotScanned"), (file.FileName, file.ContentType, file.SizeBytes, file.ScanStatus));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Pdf)), file.Sha256);
        Assert.Equal((Entity, company.Id), (file.EntityKey, file.RecordId));

        Assert.Equal(file.Id, Assert.Single(await ListAsync(client, company.Id)).Id);

        var download = await client.GetAsync($"{Url}/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString());

        var deleted = await client.DeleteAsync($"{Url}/{file.Id}?version={file.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await ListAsync(client, company.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{file.Id}/content")).StatusCode);

        // Yükleme ve silme denetim kaydına yazılır (core.file); indirme yazılmaz.
        var created = await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM core.AuditLog WHERE EntityKey = 'core.file' AND RecordId = @id AND Action = 1", ("@id", file.Id));
        var removed = await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM core.AuditLog WHERE EntityKey = 'core.file' AND RecordId = @id AND Action = 3", ("@id", file.Id));
        Assert.Equal((1, 1), (created, removed));
    }

    [Fact]
    public async Task The_storage_key_is_random_and_never_leaves_the_server()
    {
        var (tenant, _, client, company) = await StartAsync();
        var file = await UploadAsync(client, company.Id, "Gizli ad.pdf", Pdf);

        // Yanıtta anahtar ya da yol yok.
        var json = await (await client.GetAsync($"{Url}?entityKey={Entity}&recordId={company.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.BlobRoot, json, StringComparison.OrdinalIgnoreCase);

        // Diskte dosya adı, kayıt kimliği ya da dosya kimliği yoktur; yalnızca rastgele 64 karakterlik ad.
        var tenantDirectory = Path.Combine(factory.BlobRoot, tenant.Id.ToString("N"));
        var stored = Directory.EnumerateFiles(tenantDirectory, "*", SearchOption.AllDirectories).Single();
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), Path.GetFileName(stored));
        Assert.DoesNotContain(file.Id.ToString("N"), stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gizli", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(stored));
    }

    // ---- 2. Hata ve doğrulama

    [Fact]
    public async Task Invalid_uploads_are_rejected_without_leaving_content_behind()
    {
        var (tenant, _, client, company) = await StartAsync();

        async Task AssertRejectedAsync(string fileName, byte[] content, HttpStatusCode expected)
        {
            using var form = Form(company.Id, fileName, content);
            var response = await client.PostAsync(Url, form);
            Assert.True(response.StatusCode == expected, $"{fileName}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        await AssertRejectedAsync("zararli.exe", Pdf, HttpStatusCode.UnprocessableEntity); // izinsiz uzantı
        await AssertRejectedAsync("sayfa.html", Encoding.ASCII.GetBytes("<script>alert(1)</script>"), HttpStatusCode.UnprocessableEntity);
        await AssertRejectedAsync("sahte.pdf", Encoding.ASCII.GetBytes("bu bir pdf değil"), HttpStatusCode.UnprocessableEntity); // uzantı + imza uyuşmaz
        await AssertRejectedAsync("fatura.exe.pdf", Pdf, HttpStatusCode.UnprocessableEntity); // çift uzantı
        await AssertRejectedAsync("bos.pdf", [], HttpStatusCode.UnprocessableEntity);
        await AssertRejectedAsync("ikili.txt", [0x41, 0x00, 0x42], HttpStatusCode.UnprocessableEntity); // metin dosyası ikili bayt içeremez
        await AssertRejectedAsync("buyuk.pdf", [.. Pdf, .. new byte[11 * 1024 * 1024]], HttpStatusCode.RequestEntityTooLarge);

        // Reddedilen yüklemeler diskte içerik bırakmaz.
        var tenantDirectory = Path.Combine(factory.BlobRoot, tenant.Id.ToString("N"));
        Assert.False(Directory.Exists(tenantDirectory) && Directory.EnumerateFiles(tenantDirectory, "*", SearchOption.AllDirectories).Any());
        Assert.Empty(await ListAsync(client, company.Id));
    }

    [Fact]
    public async Task Duplicates_limits_and_bad_references_are_rejected()
    {
        var (_, _, client, company) = await StartAsync();
        await UploadAsync(client, company.Id, "ilk.pdf", Pdf);

        // Aynı içerik aynı kayda ikinci kez eklenemez (ad farklı olsa da); başka kayda eklenebilir.
        using (var duplicate = Form(company.Id, "ikinci-ad.pdf", Pdf))
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(Url, duplicate)).StatusCode);
        }

        var other = (await (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Diğer {Guid.NewGuid():N}"[..20]))).Content.ReadFromJsonAsync<CompanyDto>())!;
        await UploadAsync(client, other.Id, "ilk.pdf", Pdf);

        // Kayıt başına en fazla 20 dosya.
        for (var i = 2; i <= 20; i++)
        {
            await UploadAsync(client, company.Id, $"dosya-{i}.pdf", Distinct(i));
        }

        using (var over = Form(company.Id, "yirmi-bir.pdf", Distinct(99)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(Url, over)).StatusCode);
        }

        // Var olmayan kayıt, dosya eklenemeyen kayıt türü ve eksik alanlar.
        using (var missing = Form(Guid.NewGuid(), "x.pdf", Distinct(1)))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url, missing)).StatusCode);
        }

        using (var unknown = Form(company.Id, "x.pdf", Distinct(2), "yok.olmayan"))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Url, unknown)).StatusCode);
        }

        using var incomplete = new MultipartFormDataContent { { new StringContent(Entity), "entityKey" } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync(Url, incomplete)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsJsonAsync(Url, new { entityKey = Entity })).StatusCode);

        // Eski sürümle silme çakışmadır.
        var file = (await ListAsync(client, company.Id)).First();
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Url}/{file.Id}?version={file.Version + 5}")).StatusCode);
    }

    // ---- 3. Tenant izolasyonu

    [Fact]
    public async Task Another_tenants_files_are_never_listed_downloaded_deleted_or_attached_to()
    {
        var (_, _, owner, company) = await StartAsync();
        var (_, _, outsider, _) = await StartAsync();
        var file = await UploadAsync(owner, company.Id, "sir.pdf", Pdf);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Url}?entityKey={Entity}&recordId={company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);
        using (var attach = Form(company.Id, "ele-gecirdi.pdf", Distinct(7)))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync(Url, attach)).StatusCode);
        }

        // Sahibi etkilenmedi.
        Assert.Equal(file.Id, Assert.Single(await ListAsync(owner, company.Id)).Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
    }

    [Fact]
    public async Task Tenant_export_copies_file_contents_and_tenant_delete_removes_them()
    {
        var (tenant, _, client, company) = await StartAsync();
        var (other, _, otherClient, otherCompany) = await StartAsync();
        await UploadAsync(client, company.Id, "a.pdf", Pdf);
        await UploadAsync(otherClient, otherCompany.Id, "b.pdf", Distinct(3));
        var directory = Path.Combine(Path.GetTempPath(), "modulix-export-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var scope = factory.Services.CreateScope();
            var transfer = scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>();

            var export = await transfer.ExportAsync(tenant.Id, directory, CancellationToken.None);
            Assert.Contains(export.Tables, t => t is { Schema: "core", Table: "StoredFiles", Rows: 1 });
            var exported = Directory.EnumerateFiles(Path.Combine(directory, "files")).Select(Path.GetFileName).ToList();
            Assert.Single(exported);
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), exported[0]!);
            Assert.Contains("a.pdf", await File.ReadAllTextAsync(Path.Combine(directory, "core.StoredFiles.json")));
            Assert.DoesNotContain("b.pdf", await File.ReadAllTextAsync(Path.Combine(directory, "core.StoredFiles.json")));

            await transfer.DeleteAsync(tenant.Id, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(factory.BlobRoot, tenant.Id.ToString("N"))));
            Assert.True(Directory.EnumerateFiles(Path.Combine(factory.BlobRoot, other.Id.ToString("N")), "*", SearchOption.AllDirectories).Any());
            Assert.Single(await ListAsync(otherClient, otherCompany.Id));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- 4. Yetki

    [Fact]
    public async Task Access_follows_the_parent_records_access()
    {
        var (tenant, _, admin, company) = await StartAsync();
        var file = await UploadAsync(admin, company.Id, "belge.pdf", Pdf);
        var (_, reader) = await AddUserAsync(tenant, [CompaniesRead]);
        var (_, nobody) = await AddUserAsync(tenant, [ContactsRead]);

        // Salt okuma: listeler ve indirir; yükleyemez, silemez (403).
        Assert.Single(await ListAsync(reader, company.Id));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        using (var attach = Form(company.Id, "yeni.pdf", Distinct(11)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(Url, attach)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);

        // Kaydı okuma izni olmayan: dosyanın varlığı bile sızmaz (404).
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"{Url}?entityKey={Entity}&recordId={company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);

        // Hiçbiri dosyayı silemedi.
        Assert.Single(await ListAsync(admin, company.Id));
    }

    [Fact]
    public async Task Records_outside_the_data_scope_hide_their_files()
    {
        var (tenant, _, admin, company) = await StartAsync();
        var file = await UploadAsync(admin, company.Id, "kapsam.pdf", Pdf);

        // Yalnızca kendi kayıtlarını görebilen kullanıcı, başkasının firmasının dosyasını göremez.
        var (_, own) = await AddUserAsync(tenant, Everything, RecordScope.Own);
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"{Url}?entityKey={Entity}&recordId={company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await own.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);
    }

    [Fact]
    public async Task Files_are_unavailable_without_a_session_or_when_the_module_is_read_only()
    {
        var (tenant, _, client, company) = await StartAsync();
        var file = await UploadAsync(client, company.Id, "belge.pdf", Pdf);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Url}?entityKey={Entity}&recordId={company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);

        // Lisans salt okunur evreye düşünce okuma sürer, yazma kapanır (ADR-0028); süre ve ek süre bitmiş.
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(-400));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Url}/{file.Id}/content")).StatusCode);
        using var attach = Form(company.Id, "yeni.pdf", Distinct(21));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Url, attach)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Url}/{file.Id}?version={file.Version}")).StatusCode);
    }
}
