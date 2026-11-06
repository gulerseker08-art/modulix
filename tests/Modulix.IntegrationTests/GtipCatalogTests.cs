using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
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
/// ADR-0032 dilim 3d-1: GTİP kataloğu (global, sürümlü, yürürlük tarihli). Katalog tek paylaşımlı tabloda olduğundan testler birbirini
/// etkilemesin diye her test kendi yüzyıl aralığında rastgele bir yürürlük tarihi kullanır ve sorguyu o tarihe sabitler; sürümlerini
/// sonunda siler. Yönetim servis üzerinden çağrılır (Excel/CSV okuyucu 3d-2'dedir).
/// </summary>
public class GtipCatalogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly IReadOnlyList<GtipCatalogEntryInput> Tea =
    [
        new("09023000", "Siyah çay (fermente) — ambalajı 3 kg'ı geçmeyen", "kg"),
        new("090230001100", "Ambalajı 3 kg'ı geçmeyen siyah çay (ince)", "kg"),
        new("18050000", "Kakao tozu (şeker katılmamış)", "kg"),
    ];

    [Fact]
    public async Task Catalog_version_is_created_checked_searched_and_respects_effective_dates()
    {
        var s = await SetUpAsync();
        var first = Day(1000, 1199);
        var second = first.AddDays(100);
        var created = new List<Guid>();
        try
        {
            using var platform = await LoginAsync(s.PlatformAdmin);
            var v1 = await CreateAsync(s.PlatformAdmin, "Test cetveli 1", first, Tea);
            created.Add(v1.Id);
            Assert.Equal(3, v1.EntryCount);

            // İkinci sürümde "kakao" yok ve yeni bir kod var.
            var v2 = await CreateAsync(s.PlatformAdmin, "Test cetveli 2", second, [Tea[0], new GtipCatalogEntryInput("85171300", "Akıllı telefon", "adet")]);
            created.Add(v2.Id);

            // Sürüm listesi ve satır araması (kod ön eki ya da tanım içinde).
            var versions = (await platform.GetFromJsonAsync<List<GtipVersionDto>>("/api/platform/gtip/versions"))!;
            Assert.Contains(versions, v => v.Id == v1.Id && v.EntryCount == 3 && v.EffectiveFrom == first);
            var byCode = (await platform.GetFromJsonAsync<GtipEntryPageDto>($"/api/platform/gtip/versions/{v1.Id}/entries?search=0902"))!;
            Assert.Equal(["09023000", "090230001100"], byCode.Items.Select(e => e.Code));
            var byText = (await platform.GetFromJsonAsync<GtipEntryPageDto>($"/api/platform/gtip/versions/{v1.Id}/entries?search=kakao"))!;
            Assert.Equal(["18050000"], byText.Items.Select(e => e.Code));
            Assert.Equal(3, (await platform.GetFromJsonAsync<GtipEntryPageDto>($"/api/platform/gtip/versions/{v1.Id}/entries?pageSize=2"))!.TotalCount);

            using var tenant = await LoginAsync(s.Reader);

            // Aynen var, ayraçlı giriş de normalleşir; baştaki sıfır korunur.
            var found = await CheckAsync(tenant, "0902.30.00", first);
            Assert.Equal("Found", found.Status);
            Assert.Equal("Siyah çay (fermente) — ambalajı 3 kg'ı geçmeyen", found.Description);
            Assert.Equal("Test cetveli 1", found.VersionName);

            // 6 haneli üst kod tam eşleşmez ama alt kodların başıdır; olmayan kod uyarıdır.
            Assert.Equal("PrefixOnly", (await CheckAsync(tenant, "090230", first)).Status);
            Assert.Equal("NotFound", (await CheckAsync(tenant, "99999999", first)).Status);

            // Yürürlük tarihi: ikinci sürümden önce kakao vardır, sonra yoktur; yeni kod yalnızca ikinci sürümdedir.
            Assert.Equal("Found", (await CheckAsync(tenant, "18050000", second.AddDays(-1))).Status);
            var later = await CheckAsync(tenant, "18050000", second);
            Assert.Equal("NotFound", later.Status);
            Assert.Equal("Test cetveli 2", later.VersionName);
            Assert.Equal("Found", (await CheckAsync(tenant, "85171300", second)).Status);
            Assert.Equal("NotFound", (await CheckAsync(tenant, "85171300", first)).Status);

            // Öneri: kod ön eki ya da tanım içinde; kod sırasıyla.
            var byPrefix = (await tenant.GetFromJsonAsync<List<GtipEntryDto>>($"/api/gtip/search?q=0902&date={first:yyyy-MM-dd}"))!;
            Assert.Equal(["09023000", "090230001100"], byPrefix.Select(e => e.Code));
            var byName = (await tenant.GetFromJsonAsync<List<GtipEntryDto>>($"/api/gtip/search?q=telefon&date={second:yyyy-MM-dd}"))!;
            Assert.Equal(["85171300"], byName.Select(e => e.Code));
            Assert.Empty((await tenant.GetFromJsonAsync<List<GtipEntryDto>>($"/api/gtip/search?q=%20&date={first:yyyy-MM-dd}"))!);

            // Platform denetim kaydı.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.Contains("gtip.version.created", await db.PlatformAuditLog.AsNoTracking()
                .Where(a => a.ActorUserId == s.PlatformAdmin.Id).Select(a => a.Action).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    [Fact]
    public async Task Deleting_a_version_removes_its_entries_and_leaves_no_catalog_in_force()
    {
        var s = await SetUpAsync();
        var day = Day(100, 199);
        var created = new List<Guid>();
        try
        {
            var version = await CreateAsync(s.PlatformAdmin, "Silinecek", day, Tea);
            created.Add(version.Id);
            using var platform = await LoginAsync(s.PlatformAdmin);
            using var tenant = await LoginAsync(s.Reader);
            Assert.Equal("Found", (await CheckAsync(tenant, "09023000", day)).Status);

            Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"/api/platform/gtip/versions/{version.Id}")).StatusCode);
            created.Clear();

            // Sürüm yok; yürürlükte katalog kalmadığı için kod hakkında bir şey söylenmez (uyarı değil).
            Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/platform/gtip/versions/{version.Id}/entries")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await platform.DeleteAsync($"/api/platform/gtip/versions/{version.Id}")).StatusCode);
            Assert.Equal("NoCatalog", (await CheckAsync(tenant, "09023000", day)).Status);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.False(await db.GtipCatalogEntries.AsNoTracking().AnyAsync(e => e.VersionId == version.Id));
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    [Fact]
    public async Task Invalid_versions_are_rejected_without_side_effects()
    {
        var s = await SetUpAsync();
        var day = Day(1200, 1399);
        var created = new List<Guid>();
        try
        {
            using var scope = factory.Services.CreateScope();
            var admin = scope.ServiceProvider.GetRequiredService<IGtipCatalogAdmin>();
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

            // Boş liste, yinelenen kod, boş ad: hiçbir sürüm yazılmaz.
            await Assert.ThrowsAsync<ArgumentException>(() => admin.CreateVersionAsync(s.PlatformAdmin.Id, new("Boş", day, null), [], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => admin.CreateVersionAsync(
                s.PlatformAdmin.Id, new("Çift", day, null), [Tea[0], new GtipCatalogEntryInput(Tea[0].Code, "Aynı kod", null)], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => admin.CreateVersionAsync(s.PlatformAdmin.Id, new("  ", day, null), Tea, CancellationToken.None));
            Assert.False(await db.GtipCatalogVersions.AsNoTracking().AnyAsync(v => v.EffectiveFrom == day));

            // Aynı yürürlük tarihli ikinci sürüm çakışır ve ilk sürümün satırları değişmez.
            var first = await admin.CreateVersionAsync(s.PlatformAdmin.Id, new("İlk", day, "ilk.xlsx"), Tea, CancellationToken.None);
            created.Add(first.Id);
            await Assert.ThrowsAsync<GtipCatalogConflictException>(() => admin.CreateVersionAsync(s.PlatformAdmin.Id, new("İkinci", day, null), [Tea[0]], CancellationToken.None));
            Assert.Equal(3, await db.GtipCatalogEntries.AsNoTracking().CountAsync(e => e.VersionId == first.Id));
            Assert.Single(await db.GtipCatalogVersions.AsNoTracking().Where(v => v.EffectiveFrom == day).ToListAsync());

            // Okuma uçları: biçimi bozuk kod 400, bilinmeyen sürüm 404.
            using var tenant = await LoginAsync(s.Reader);
            Assert.Equal(HttpStatusCode.BadRequest, (await tenant.GetAsync("/api/gtip/check?code=abc")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await tenant.GetAsync("/api/gtip/check?code=1234")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await tenant.GetAsync("/api/gtip/check")).StatusCode);
            using var platform = await LoginAsync(s.PlatformAdmin);
            Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/platform/gtip/versions/{Guid.NewGuid()}/entries")).StatusCode);
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    [Fact]
    public async Task Catalog_is_shared_read_only_across_tenants_and_management_is_platform_only()
    {
        var s = await SetUpAsync();
        var day = Day(1400, 1599);
        var created = new List<Guid>();
        try
        {
            var version = await CreateAsync(s.PlatformAdmin, "Ortak katalog", day, Tea);
            created.Add(version.Id);

            // Katalog tenant verisi değildir: iki ayrı tenant aynı sürümü görür; depo modülü kapalı tenant okuyamaz.
            using var readerA = await LoginAsync(s.Reader);
            using var readerB = await LoginAsync(s.OtherTenantReader);
            Assert.Equal("Found", (await CheckAsync(readerA, "09023000", day)).Status);
            Assert.Equal("Found", (await CheckAsync(readerB, "09023000", day)).Status);
            using var withoutModule = await LoginAsync(s.NoModuleUser);
            Assert.Equal(HttpStatusCode.Forbidden, (await withoutModule.GetAsync($"/api/gtip/check?code=09023000&date={day:yyyy-MM-dd}")).StatusCode);

            // Tesis izni stok kartı izni vermez; okuyucu katalog yönetemez.
            using var facilityOnly = await LoginAsync(s.FacilityOnly);
            Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync($"/api/gtip/check?code=09023000&date={day:yyyy-MM-dd}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readerA.GetAsync("/api/platform/gtip/versions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readerA.GetAsync($"/api/platform/gtip/versions/{version.Id}/entries")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readerA.DeleteAsync($"/api/platform/gtip/versions/{version.Id}")).StatusCode);

            // Oturumsuz çağrı oturum ister.
            using var anonymous = factory.CreateBrowserClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/gtip/check?code=09023000&date={day:yyyy-MM-dd}")).StatusCode);
        }
        finally
        {
            await CleanUpAsync(s.PlatformAdmin, created);
        }
    }

    private static DateOnly Day(int fromYear, int toYear) =>
        new DateOnly(Random.Shared.Next(fromYear, toYear + 1), 1, 1).AddDays(Random.Shared.Next(0, 360));

    private async Task<GtipCatalogVersionView> CreateAsync(ModulixUser actor, string name, DateOnly day, IReadOnlyList<GtipCatalogEntryInput> entries)
    {
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IGtipCatalogAdmin>();
        return await admin.CreateVersionAsync(actor.Id, new GtipCatalogVersionInput(name, day, "test.xlsx"), entries, CancellationToken.None);
    }

    private async Task CleanUpAsync(ModulixUser actor, IEnumerable<Guid> versionIds)
    {
        foreach (var id in versionIds.ToList())
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IGtipCatalogAdmin>().DeleteVersionAsync(actor.Id, id, CancellationToken.None);
        }
    }

    private static async Task<GtipCheckDto> CheckAsync(HttpClient client, string code, DateOnly day)
    {
        var response = await client.GetAsync($"/api/gtip/check?code={Uri.EscapeDataString(code)}&date={day:yyyy-MM-dd}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GtipCheckDto>())!;
    }

    private async Task<(ModulixUser PlatformAdmin, ModulixUser Reader, ModulixUser OtherTenantReader, ModulixUser FacilityOnly, ModulixUser NoModuleUser)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var reader = await factory.CreateUserAsync(tenant);
        var facilityOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [StockItemsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead], Scope.All, facilityOnly);

        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "warehouse", enabled: true);
        var otherReader = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [StockItemsRead], Scope.All, otherReader);

        // Depo modülü açılmamış tenant: kullanıcı izin taşısa da modül kapalıdır.
        var closed = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(closed.Id, "warehouse", enabled: false);
        var noModule = await factory.CreateUserAsync(closed);
        await factory.CreateRoleAsync(closed.Id, [StockItemsRead], Scope.All, noModule);

        var platformAdmin = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(platformAdmin.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return (platformAdmin, reader, otherReader, facilityOnly, noModule);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
