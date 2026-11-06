using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLibs;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Customs;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 3d-2: GTİP kataloğunu Excel (.xlsx) ve CSV dosyasından içe aktarma. Gerçek bir .xlsx üretilip uçlara gönderilir.
/// Katalog tek paylaşımlı tabloda olduğundan her test kendi yüzyıl aralığında rastgele yürürlük tarihi kullanır ve sürümlerini siler.
/// </summary>
public class GtipImportTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Header = ["GTİP", "Tanım", "Birim"];

    [Fact]
    public async Task Excel_and_csv_files_are_previewed_imported_and_checked_against_effective_dates()
    {
        var s = await SetUpAsync();
        var first = Day(1600, 1699);
        var second = first.AddDays(50);
        var created = new List<Guid>();
        try
        {
            using var platform = await LoginAsync(s.PlatformAdmin);
            using var tenant = await LoginAsync(s.Reader);

            // Fasıl başlığı atlanır; sayı olarak kaydedilmiş kodun baştaki sıfırı tamamlanır; ayraçlı metin kod normalleşir.
            var xlsx = Workbook(
                new Dictionary<string, object?> { ["GTİP"] = "09", ["Tanım"] = "Kahve, çay ve baharat", ["Birim"] = null },
                new Dictionary<string, object?> { ["GTİP"] = 9023000, ["Tanım"] = "Siyah çay (fermente)", ["Birim"] = "kg" },
                new Dictionary<string, object?> { ["GTİP"] = "1805.00.00", ["Tanım"] = "Kakao tozu", ["Birim"] = "kg" },
                new Dictionary<string, object?> { ["GTİP"] = "8517.13.00", ["Tanım"] = "Akıllı telefon", ["Birim"] = "adet" });

            var preview = await PreviewAsync(platform, xlsx, "tarife.xlsx");
            Assert.Null(preview.FatalError);
            Assert.True(preview.CanImport);
            Assert.Equal((4, 3, 1, 1, 0), (preview.RowsRead, preview.ValidCount, preview.HeadingRows, preview.PaddedCodes, preview.ErrorCount));
            Assert.Equal(["09023000", "18050000", "85171300"], preview.Sample.Select(e => e.Code));

            // Önizleme hiçbir şey yazmaz.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
                Assert.False(await db.GtipCatalogVersions.AsNoTracking().AnyAsync(v => v.EffectiveFrom == first));
            }

            var v1 = await ImportAsync(platform, xlsx, "tarife.xlsx", "2026 cetveli (xlsx)", first);
            created.Add(v1.Id);
            Assert.Equal((3, "tarife.xlsx", first), (v1.EntryCount, v1.SourceFileName, v1.EffectiveFrom));
            var found = await CheckAsync(tenant, "0902.30.00", first);
            Assert.Equal(("Found", "Siyah çay (fermente)", "2026 cetveli (xlsx)"), (found.Status, found.Description, found.VersionName));

            // Türkçe Excel'in "CSV olarak kaydet" çıktısı: noktalı virgül ve Windows-1254. Sonraki sürümde yalnızca iki kod vardır.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var csv = Encoding.GetEncoding(1254).GetBytes("GTİP;Eşya Tanımı;Ölçü Birimi\r\n0902.30.00;Siyah çay (fermente);kg\r\n8517.13.00;Akıllı telefon (ığ);adet\r\n");
            var csvPreview = await PreviewAsync(platform, csv, "tarife.csv");
            Assert.True(csvPreview.CanImport);
            var v2 = await ImportAsync(platform, csv, "tarife.csv", "2026 cetveli (csv)", second);
            created.Add(v2.Id);
            Assert.Equal(2, v2.EntryCount);
            Assert.Equal("Found", (await CheckAsync(tenant, "18050000", second.AddDays(-1))).Status);
            Assert.Equal("NotFound", (await CheckAsync(tenant, "18050000", second)).Status);
            Assert.Equal("Akıllı telefon (ığ)", (await CheckAsync(tenant, "85171300", second)).Description);

            var entries = (await platform.GetFromJsonAsync<GtipEntryPageDto>($"/api/platform/gtip/versions/{v2.Id}/entries"))!;
            Assert.Equal(["09023000", "85171300"], entries.Items.Select(e => e.Code));
            Assert.Equal("adet", entries.Items[1].Unit);
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    [Fact]
    public async Task Bad_files_and_missing_fields_are_rejected_without_creating_a_version()
    {
        var s = await SetUpAsync();
        var day = Day(1800, 1899);
        var created = new List<Guid>();
        try
        {
            using var platform = await LoginAsync(s.PlatformAdmin);

            // Arayüzün gösterdiği dosya sınırı (Shared) ile sunucunun uyguladığı sınır aynı olmalı.
            Assert.Equal(GtipImportRules.MaxFileBytes, GtipImportLimits.MaxFileBytes);

            // Hatalı satırlar önizlemede satır numarasıyla görünür ve içe aktarmayı engeller (kısmi katalog yüklenmez).
            var bad = Workbook(
                new Dictionary<string, object?> { ["GTİP"] = "09023000", ["Tanım"] = "Siyah çay", ["Birim"] = "kg" },
                new Dictionary<string, object?> { ["GTİP"] = "ABC123456", ["Tanım"] = "Harfli kod", ["Birim"] = null },
                new Dictionary<string, object?> { ["GTİP"] = "18050000", ["Tanım"] = null, ["Birim"] = null },
                new Dictionary<string, object?> { ["GTİP"] = "09023000", ["Tanım"] = "Yinelenen", ["Birim"] = null });
            var preview = await PreviewAsync(platform, bad, "hatali.xlsx");
            Assert.False(preview.CanImport);
            Assert.Equal((1, 3), (preview.ValidCount, preview.ErrorCount));
            Assert.Equal([3, 4, 5], preview.Errors.Select(e => e.Row));
            var rejected = await platform.PostAsync(ImportUrl("hatali.xlsx", "Hatalı", day), Raw(bad));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("3 hatalı satır", await rejected.Content.ReadAsStringAsync());

            // Metin dosyası .xlsx adıyla, eski .xls, desteklenmeyen uzantı, boş dosya, başlıksız dosya: önizleme açıklayıcı hata döner.
            Assert.Contains(".xlsx", (await PreviewAsync(platform, Encoding.UTF8.GetBytes("GTİP;Tanım\n09023000;Çay"), "sahte.xlsx")).FatalError);
            Assert.Contains(".xls", (await PreviewAsync(platform, [1, 2, 3, 4], "eski.xls")).FatalError);
            Assert.Contains("biçimleri", (await PreviewAsync(platform, [1, 2, 3, 4], "tarife.pdf")).FatalError);
            Assert.Contains("boş", (await PreviewAsync(platform, [], "bos.csv")).FatalError);
            var noHeader = await PreviewAsync(platform, Encoding.UTF8.GetBytes("a;b\n09023000;Çay"), "basliksiz.csv");
            Assert.Contains("GTİP", noHeader.FatalError);
            Assert.False(noHeader.CanImport);
            var headerOnly = await PreviewAsync(platform, Encoding.UTF8.GetBytes("GTİP;Tanım\n"), "bos-liste.csv");
            Assert.False(headerOnly.CanImport);

            // Ad ve yürürlük tarihi zorunludur; geçersiz tarih ve boş gövde 400.
            var good = Encoding.UTF8.GetBytes("GTİP;Tanım\n09023000;Çay\n");
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsync("/api/platform/gtip/versions?fileName=a.csv&effectiveFrom=" + day.ToString("yyyy-MM-dd"), Raw(good))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsync("/api/platform/gtip/versions?fileName=a.csv&name=Ad", Raw(good))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsync("/api/platform/gtip/versions?fileName=a.csv&name=Ad&effectiveFrom=bozuk", Raw(good))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsync(ImportUrl("bos.csv", "Ad", day), Raw([]))).StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
                Assert.False(await db.GtipCatalogVersions.AsNoTracking().AnyAsync(v => v.EffectiveFrom == day));
            }

            // Aynı yürürlük tarihine ikinci yükleme 409'dur ve ilk sürümü bozmaz.
            var first = await ImportAsync(platform, good, "a.csv", "İlk", day);
            created.Add(first.Id);
            var conflict = await platform.PostAsync(ImportUrl("b.csv", "İkinci", day), Raw(good));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(1, (await platform.GetFromJsonAsync<List<GtipVersionDto>>("/api/platform/gtip/versions"))!.Count(v => v.EffectiveFrom == day));
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    [Fact]
    public async Task Only_platform_administrators_can_preview_or_import()
    {
        var s = await SetUpAsync();
        var day = Day(1900, 1999);
        var csv = Encoding.UTF8.GetBytes("GTİP;Tanım\n09023000;Çay\n");

        // Yetkili tenant kullanıcısı bile (stok kartı okuma izni) katalog yükleyemez; oturumsuz çağrı oturum ister.
        using var tenant = await LoginAsync(s.Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostAsync("/api/platform/gtip/versions/preview?fileName=a.csv", Raw(csv))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostAsync(ImportUrl("a.csv", "Yetkisiz", day), Raw(csv))).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(ImportUrl("a.csv", "Oturumsuz", day), Raw(csv))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.GtipCatalogVersions.AsNoTracking().AnyAsync(v => v.EffectiveFrom == day));
    }

    private static byte[] Workbook(params Dictionary<string, object?>[] rows)
    {
        using var stream = new MemoryStream();
        MiniExcel.SaveAs(stream, rows.ToList(), printHeader: true, sheetName: "Tarife", excelType: ExcelType.XLSX);
        return stream.ToArray();
    }

    private static ByteArrayContent Raw(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static string ImportUrl(string fileName, string name, DateOnly day) =>
        $"/api/platform/gtip/versions?fileName={Uri.EscapeDataString(fileName)}&name={Uri.EscapeDataString(name)}&effectiveFrom={day:yyyy-MM-dd}";

    private static async Task<GtipImportPreviewDto> PreviewAsync(HttpClient platform, byte[] bytes, string fileName)
    {
        var response = await platform.PostAsync($"/api/platform/gtip/versions/preview?fileName={Uri.EscapeDataString(fileName)}", Raw(bytes));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GtipImportPreviewDto>())!;
    }

    private static async Task<GtipVersionDto> ImportAsync(HttpClient platform, byte[] bytes, string fileName, string name, DateOnly day)
    {
        var response = await platform.PostAsync(ImportUrl(fileName, name, day), Raw(bytes));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GtipVersionDto>())!;
    }

    private static async Task<GtipCheckDto> CheckAsync(HttpClient client, string code, DateOnly day)
    {
        var response = await client.GetAsync($"/api/gtip/check?code={Uri.EscapeDataString(code)}&date={day:yyyy-MM-dd}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GtipCheckDto>())!;
    }

    private static DateOnly Day(int fromYear, int toYear) =>
        new DateOnly(Random.Shared.Next(fromYear, toYear + 1), 1, 1).AddDays(Random.Shared.Next(0, 360));

    private async Task CleanUpAsync(ModulixUser actor, IEnumerable<Guid> versionIds)
    {
        foreach (var id in versionIds.ToList())
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IGtipCatalogAdmin>().DeleteVersionAsync(actor.Id, id, CancellationToken.None);
        }
    }

    private async Task<(ModulixUser PlatformAdmin, ModulixUser Reader)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [StockItemsRead], Scope.All, reader);

        var platformAdmin = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(platformAdmin.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return (platformAdmin, reader);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
