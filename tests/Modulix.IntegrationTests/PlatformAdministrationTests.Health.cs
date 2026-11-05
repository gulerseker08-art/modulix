using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Platform;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 6 (Y4.3): platform sağlık ve kullanım raporu. Yalnızca sayı, zaman ve firma adı verir; ödeme bilgisi, firma kaydı,
/// dosya ve e-posta içeriği yoktur, bu yüzden Destek de dahil her platform rolü okur; firma kullanıcısı ve anonim okuyamaz.
/// </summary>
public partial class PlatformAdministrationTests
{
    [Fact]
    public async Task Health_report_shows_queues_version_usage_and_security_counts_to_every_platform_role()
    {
        var active = await factory.CreateTenantAsync();
        var dormant = await factory.CreateTenantAsync();
        await factory.CreateUserAsync(active);
        await factory.CreateUserAsync(dormant);
        var supportUser = await CreatePlatformMemberAsync(PlatformRole.Support);
        using var support = await LoginAsync(supportUser);
        using var owner = await LoginAsync(await CreatePlatformAdministratorAsync());

        // Bir platform işlemi güvenlik sayacına yansır.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/platform/tenants/{dormant.Id}/suspend", new TenantReasonRequest("Sağlık testi"))).StatusCode);

        foreach (var client in new[] { support, owner })
        {
            var health = (await client.GetFromJsonAsync<PlatformHealthResponse>("/api/platform/health"))!;

            Assert.False(string.IsNullOrWhiteSpace(health.Version.Application));
            Assert.Contains(".NET", health.Version.Runtime);
            Assert.NotNull(health.Version.LastMigration);
            Assert.Equal(0, health.Version.PendingMigrations);
            Assert.True(health.Email.Pending >= 0 && health.Email.Failed >= 0);
            Assert.True(health.Events.Pending >= 0);
            Assert.InRange(health.EventDeliveries24Hours.FailureRate, 0, 1);
            Assert.InRange(health.Emails24Hours.FailureRate, 0, 1);
            Assert.True(health.Usage.OperationalTenants >= 1);
            Assert.True(health.Usage.ActiveMembers >= 1);
            Assert.True(health.SecurityEvents7Days.Single(e => e.Action == "tenant.suspended").Count >= 1);
        }
    }

    [Fact]
    public async Task Health_report_lists_dormant_open_tenants_but_never_closed_ones_and_carries_no_tenant_records()
    {
        var dormant = await factory.CreateTenantAsync();
        var recent = await factory.CreateTenantAsync();
        var closed = await factory.CreateTenantAsync();
        var dormantUser = await factory.CreateUserAsync(dormant);
        var recentUser = await factory.CreateUserAsync(recent);
        await factory.CreateUserAsync(closed);
        using var owner = await LoginAsync(await CreatePlatformAdministratorAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE platform.Memberships SET LastSignInAt = DATEADD(DAY, -45, SYSUTCDATETIME()) WHERE TenantId = {dormant.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE platform.Memberships SET LastSignInAt = SYSUTCDATETIME() WHERE TenantId = {recent.Id}");
        }

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/platform/tenants/{closed.Id}/close", new TenantReasonRequest("Kapalı"))).StatusCode);

        var raw = await owner.GetStringAsync("/api/platform/health");
        var health = JsonSerializer.Deserialize<PlatformHealthResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var names = health.InactiveTenants.Select(t => t.TenantId).ToList();

        // Liste en eski 10 firmayı gösterir (paylaşılan veritabanında başka firmalar da vardır); girişi yeni olan ve kapalı firma listelenmez,
        // listedekilerin hepsinin son girişi yok ya da 30 günden eskidir.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        Assert.All(health.InactiveTenants, t => Assert.True(t.LastSignInAt is null || t.LastSignInAt < cutoff));
        Assert.True(health.Usage.SignedInTenants7Days >= 1); // yeni girişli firma sayılır
        Assert.DoesNotContain(recent.Id, names);
        Assert.DoesNotContain(closed.Id, names);
        Assert.True(health.InactiveTenants.Count <= 10);

        // Rapor firma kullanıcılarının adını, e-postasını ya da kayıtlarını taşımaz.
        Assert.DoesNotContain(dormantUser.Email!, raw);
        Assert.DoesNotContain(recentUser.Email!, raw);
        Assert.DoesNotContain(dormantUser.DisplayName, raw);
    }

    [Fact]
    public async Task Health_report_is_closed_to_tenant_administrators_and_anonymous_callers()
    {
        var tenant = await factory.CreateTenantAsync();
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var tenantClient = await LoginAsync(tenantAdmin);
        using var anonymous = factory.CreateBrowserClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync("/api/platform/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/health")).StatusCode);
    }
}
