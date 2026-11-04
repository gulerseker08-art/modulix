using System.Net;
using System.Net.Http.Json;
using Modulix.Modules.Crm;
using Modulix.Shared.Access;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 3: modül hakkının bitişi. Bitişten sonra ek süre (14 gün) boyunca yazma açık kalır, ek süre de bitince modül
/// salt okunur olur: okuma çalışır, yazma sunucuda reddedilir, veri silinmez. Evre saklanmaz, bitiş tarihinden her istekte
/// hesaplanır; bir firmanın süresi başka firmayı etkilemez.
/// </summary>
public partial class PlatformAdministrationTests
{
    private static readonly string[] CompanyPermissions = [CompaniesRead, CompaniesWrite];

    [Fact]
    public async Task Writes_stay_open_during_the_grace_period_stop_when_it_ends_and_reads_continue_until_the_platform_extends()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, CompanyPermissions, user);
        using var client = await LoginAsync(user);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);

        // Bitiş gelmedi: uyarı yok (30 günden uzak), yazma açık.
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(90));
        Assert.Empty((await Access(client)).Licenses);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Açık Ltd."))).StatusCode);

        // Bitişe 10 gün kala uyarı görünür, evre hâlâ Active.
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(10));
        Assert.Equal(LicensePhase.Active, Assert.Single((await Access(client)).Licenses).Phase);

        // Ek süre (bitiş 3 gün önce): yazma sürer, uyarı Grace, salt okunura geçiş tarihi bitiş + 14 gündür.
        var ended = DateTimeOffset.UtcNow.AddDays(-3);
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, ended);
        var grace = Assert.Single((await Access(client)).Licenses);
        Assert.Equal(LicensePhase.Grace, grace.Phase);
        Assert.Equal(ended.AddDays(14).ToUnixTimeSeconds(), grace.GraceEndsAt.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Ek Süre Ltd."))).StatusCode);

        // Ek süre de bitti (bitiş 20 gün önce): okuma çalışır, her yazma yöntemi 403 döner, hiçbir kayıt eklenmez.
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(-20));
        Assert.Equal(LicensePhase.ReadOnly, Assert.Single((await Access(client)).Licenses).Phase);
        var before = (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!;
        Assert.Equal(2, before.Items.Count);

        var blocked = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Reddedilecek Ltd."));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("salt okunur", await blocked.Content.ReadAsStringAsync());
        var someId = before.Items[0].Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/crm/companies/{someId}", new SaveCompanyRequest("Yeni Ad"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/crm/companies/{someId}")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items.Count);

        // Platform tarafı durumu görür: süre takviminde Salt okunur, gerekçesiz uzatma reddedilir.
        var listed = (await platform.GetFromJsonAsync<List<PlatformExpirationDto>>("/api/platform/expirations?days=30"))!
            .Single(e => e.TenantId == tenant.Id);
        Assert.Equal(LicensePhase.ReadOnly, listed.Phase);
        var url = $"/api/platform/tenants/{tenant.Id}/modules/{CrmModule.ModuleKey}";
        var until = DateTimeOffset.UtcNow.AddDays(30);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, until))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await platform.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, until, Reason: "Yenileme"))).StatusCode);

        // Uzatılınca yazma hemen geri gelir; veri baştan beri yerindeydi.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Yenilenmiş Ltd."))).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items.Count);
    }

    [Fact]
    public async Task One_tenants_expired_licence_does_not_affect_another_tenant_or_other_modules()
    {
        var expired = await factory.CreateTenantAsync();
        var healthy = await factory.CreateTenantAsync();
        foreach (var tenant in new[] { expired, healthy })
        {
            await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
            await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        }

        var expiredUser = await factory.CreateUserAsync(expired);
        var healthyUser = await factory.CreateUserAsync(healthy);
        await factory.CreateRoleAsync(expired.Id, CompanyPermissions, expiredUser);
        await factory.CreateRoleAsync(healthy.Id, CompanyPermissions, healthyUser);
        using var expiredClient = await LoginAsync(expiredUser);
        using var healthyClient = await LoginAsync(healthyUser);

        await factory.SetEntitlementEndAsync(expired.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(HttpStatusCode.Forbidden, (await expiredClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("A Ltd."))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await healthyClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("B Ltd."))).StatusCode);
        Assert.Empty((await Access(healthyClient)).Licenses);

        // Aynı firmanın süresi dolmayan modülü etkilenmez: yalnızca bitişi geçen modül salt okunur olur.
        var licenses = (await Access(expiredClient)).Licenses;
        Assert.Equal(CrmModule.ModuleKey, Assert.Single(licenses).ModuleKey);
        Assert.Contains("contracts", (await Access(expiredClient)).EnabledModules);
    }

    [Fact]
    public async Task Read_only_does_not_widen_access_a_user_without_the_permission_is_still_refused_and_a_closed_module_stays_closed()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var reader = await factory.CreateUserAsync(tenant);
        var stranger = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], reader);
        using var readerClient = await LoginAsync(reader);
        using var strangerClient = await LoginAsync(stranger);

        // Ek sürede bile izni olmayan okuyamaz; izni olan okur ama yazma izni yoktur.
        await factory.SetEntitlementEndAsync(tenant.Id, CrmModule.ModuleKey, DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Yetkisiz Ltd."))).StatusCode);

        // Kapalı modül salt okunur değil, tamamen kapalıdır (okuma da 403).
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/modules/{CrmModule.ModuleKey}", new SetTenantModuleRequest(false, Reason: "Sözleşme bitti"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Empty((await Access(readerClient)).Licenses);
    }

    [Fact]
    public async Task Preview_endpoints_that_write_nothing_keep_working_in_a_read_only_module()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ["support.sla.manage"], user);
        using var client = await LoginAsync(user);
        await factory.SetEntitlementEndAsync(tenant.Id, "support", DateTimeOffset.UtcNow.AddDays(-20));

        // Önizleme (POST olsa da) salt okunurda çalışır: lisans nedeniyle reddedilmez; politika yazmak reddedilir.
        var preview = await client.PostAsJsonAsync("/api/support/sla/preview", new { typeKey = "incident", priority = "Normal" });
        Assert.NotEqual(HttpStatusCode.Forbidden, preview.StatusCode);
        var write = await client.PostAsJsonAsync("/api/support/sla-policies", new { });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("salt okunur", await write.Content.ReadAsStringAsync());
    }

    private static async Task<MyAccessResponse> Access(HttpClient client) =>
        (await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access"))!;
}
