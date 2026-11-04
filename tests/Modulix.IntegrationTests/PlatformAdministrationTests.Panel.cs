using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Platform;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 2: gerekçeli işlemler (kapatma, modül kapatma, süre uzatma), genel bakış, süre takvimi ve firma listesinde
/// yönetici sayısının okunmaması. Platform ekranlarının hepsi yalnızca platform yöneticisine açıktır.
/// </summary>
public partial class PlatformAdministrationTests
{
    [Fact]
    public async Task Closing_a_tenant_and_disabling_a_module_need_a_reason_and_the_reason_is_recorded()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        // Gerekçesiz kapatma ve gövdesiz çağrı reddedilir; firma açık kalır.
        var blank = await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/close", new TenantReasonRequest("  "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("reason", await blank.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/close", null)).StatusCode);
        Assert.Equal(TenantState.Active, (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Tenant.Status);

        // Modülü gerekçesiz kapatmak reddedilir, açık kalır; gerekçeyle kapanır ve platform kaydına yazılır.
        var url = $"/api/platform/tenants/{tenant.Id}/modules/crm";
        var noReason = await client.PutAsJsonAsync(url, new SetTenantModuleRequest(false));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("reason", await noReason.Content.ReadAsStringAsync());
        Assert.Contains("crm", (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Tenant.EnabledModules);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(false, Reason: "Sözleşme sona erdi"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/close", new TenantReasonRequest("Müşteri ayrıldı"))).StatusCode);
        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}");
        Assert.Contains(audit!.Items, i => i.Action == "tenant.module-changed" && i.Reason == "Sözleşme sona erdi");
        Assert.Contains(audit.Items, i => i.Action == "tenant.closed" && i.Reason == "Müşteri ayrıldı");
        Assert.All(audit.Items, i => Assert.Equal(tenant.Name, i.TargetTenantName));
    }

    [Fact]
    public async Task Extending_a_licence_needs_a_reason_but_shortening_or_first_dating_does_not()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var url = $"/api/platform/tenants/{tenant.Id}/modules/crm";
        var in30 = DateTimeOffset.UtcNow.AddDays(30);

        // Süresiz hakka ilk kez tarih vermek bir uzatma değildir.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, in30))).StatusCode);

        // Bitişi ileri almak ya da kaldırmak gerekçe ister; kayıt değişmez.
        var extend = await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, in30.AddDays(30)));
        Assert.Equal(HttpStatusCode.BadRequest, extend.StatusCode);
        Assert.Contains("reason", await extend.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, null, "süresiz"))).StatusCode);
        Assert.Equal(in30, (await Entitlement(client, tenant.Id)).ValidUntil);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, in30.AddDays(30), Reason: "Yenileme sözleşmesi"))).StatusCode);
        Assert.Equal(in30.AddDays(30), (await Entitlement(client, tenant.Id)).ValidUntil);

        // Kısaltmak gerekçe istemez.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Plan, in30))).StatusCode);
        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.module-changed");
        Assert.Contains(audit!.Items, i => i.Reason == "Yenileme sözleşmesi");
    }

    [Fact]
    public async Task Expirations_list_the_open_dated_rights_of_open_tenants_and_the_overview_counts_them()
    {
        var soon = await factory.CreateTenantAsync();
        var later = await factory.CreateTenantAsync();
        var overdue = await factory.CreateTenantAsync();
        var undated = await factory.CreateTenantAsync();
        var closed = await factory.CreateTenantAsync();
        foreach (var tenant in new[] { soon, later, overdue, undated, closed })
        {
            await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        }

        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var before = (await client.GetFromJsonAsync<PlatformOverviewResponse>("/api/platform/overview"))!;

        async Task DateAsync(Guid id, int days) => Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/platform/tenants/{id}/modules/crm", new SetTenantModuleRequest(true, LicenseSource.Plan, DateTimeOffset.UtcNow.AddDays(days)))).StatusCode);
        await DateAsync(soon.Id, 10);
        await DateAsync(later.Id, 70);
        await DateAsync(overdue.Id, 5);
        await DateAsync(closed.Id, 10);

        // Bitişi geçmiş hak: API geçmiş tarih kabul etmez, bu yüzden tarih sonradan geriye alınır (süre dolumu uygulanmaz, Dilim 3).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE platform.ModuleEntitlements SET ValidUntil = DATEADD(DAY, -3, SYSUTCDATETIME()) WHERE TenantId = {overdue.Id} AND ModuleKey = N'crm'");
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{closed.Id}/close", new TenantReasonRequest("Kapalı firma"))).StatusCode);

        var ours = new HashSet<Guid> { soon.Id, later.Id, overdue.Id, undated.Id, closed.Id };
        var in90 = (await client.GetFromJsonAsync<List<PlatformExpirationDto>>("/api/platform/expirations?days=90"))!.Where(e => ours.Contains(e.TenantId)).ToList();

        // Süresiz ve kapalı firmanın hakkı listelenmez; geçmiş tarihli en üstte.
        Assert.Equal([overdue.Id, soon.Id, later.Id], in90.Select(e => e.TenantId));
        Assert.All(in90, e => Assert.Equal("crm", e.ModuleKey));
        Assert.Equal(LicenseSource.Plan, in90[1].Source);
        Assert.Equal(soon.Name, in90[1].TenantName);

        var in30 = (await client.GetFromJsonAsync<List<PlatformExpirationDto>>("/api/platform/expirations?days=30"))!.Where(e => ours.Contains(e.TenantId));
        Assert.Equal([overdue.Id, soon.Id], in30.Select(e => e.TenantId));

        // Genel bakış paylaşılan veritabanında başka testlerin satırlarını da sayar; bu testin eklediği fark aranır.
        var after = (await client.GetFromJsonAsync<PlatformOverviewResponse>("/api/platform/overview"))!;
        Assert.Equal(before.Expired + 1, after.Expired);
        Assert.Equal(before.ExpiringIn30Days + 1, after.ExpiringIn30Days);
        Assert.Equal(before.ExpiringIn60Days + 1, after.ExpiringIn60Days);
        Assert.Equal(before.ExpiringIn90Days + 2, after.ExpiringIn90Days);
        Assert.Equal(Enum.GetValues<TenantState>().Length, after.TenantCounts.Count);
        Assert.True(after.TenantCounts.Single(c => c.State == TenantState.Closed).Count >= 1);
    }

    [Fact]
    public async Task Tenant_list_does_not_read_administrator_counts_but_the_detail_does()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        var listed = Assert.Single((await client.GetFromJsonAsync<PlatformTenantPageResponse>($"/api/platform/tenants?search={tenant.Slug}"))!.Items);
        Assert.Null(listed.AdministratorCount);
        Assert.Equal(1, listed.ActiveMemberCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Tenant.AdministratorCount);
    }

    [Fact]
    public async Task Tenant_administrators_and_anonymous_callers_cannot_use_overview_expirations_or_reasoned_changes()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var tenantClient = await LoginAsync(tenantAdmin);
        using var anonymous = factory.CreateBrowserClient();

        foreach (var path in new[] { "/api/platform/overview", "/api/platform/expirations", "/api/platform/expirations?days=30" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        // Gerekçe verilse de firma yöneticisi kendi firmasını kapatamaz ve modülünü kapatamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/close", new TenantReasonRequest("Kendi firmam"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/modules/crm", new SetTenantModuleRequest(false, Reason: "Kendi firmam"))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(Modulix.Core.Platform.TenantStatus.Active, (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id)).Status);
        Assert.True(await db.ModuleEntitlements.AsNoTracking().AnyAsync(e => e.TenantId == tenant.Id && e.ModuleKey == "crm" && e.IsEnabled));
    }

    private static async Task<TenantEntitlementDto> Entitlement(HttpClient client, Guid tenantId) =>
        Assert.Single(
            (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenantId}"))!.Entitlements!,
            e => e.ModuleKey == "crm");
}
