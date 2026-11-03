using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Communication;
using Modulix.Api.Tenancy;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Communication;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Communication;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 12: gönderim günlüğü temizliği. Süresi dolmasının üstünden 365 gün geçmiş bağlantı belirteçleri ve 7 günden eski sıklık pencereleri silinir; yakın zamanda
/// süresi dolan belirteç (çıkış hâlâ kabul edilir) ve bugünün pencereleri kalır. Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki.
/// </summary>
public class CommunicationCleanupTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public CommunicationCleanupTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        host = factory.WithWebHostBuilder(builder => builder.UseSetting("App:PublicUrl", "https://app.modulix.test"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    private CommunicationCleanupRunner Runner => host.Services.GetRequiredService<CommunicationCleanupRunner>();

    [Fact]
    public async Task Old_tokens_and_frequency_windows_are_deleted_and_recent_ones_are_kept()
    {
        var tenant = await factory.CreateTenantAsync();
        var seeded = await SeedAsync(tenant.Id);

        var result = await Runner.RunTenantAsync(tenant.Id, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal((1, 1, false), (result!.Tokens, result.FrequencyWindows, result.More));

        using var scope = TenantScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var hashes = await db.CommunicationLinkTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync();
        Assert.DoesNotContain(LinkTokens.Hash(seeded.Ancient), hashes);
        Assert.Contains(LinkTokens.Hash(seeded.RecentlyExpired), hashes);
        Assert.Contains(LinkTokens.Hash(seeded.Fresh), hashes);
        Assert.Equal(2, await db.FrequencyWindows.AsNoTracking().CountAsync());   // 3 gün önceki ve bugünkü
    }

    [Fact]
    public async Task A_second_run_deletes_nothing_and_a_deleted_link_is_unknown_while_a_recently_expired_one_still_works()
    {
        var tenant = await factory.CreateTenantAsync();
        var seeded = await SeedAsync(tenant.Id);
        Assert.NotNull(await Runner.RunTenantAsync(tenant.Id, CancellationToken.None));

        var again = await Runner.RunTenantAsync(tenant.Id, CancellationToken.None);
        Assert.Equal((0, 0, false), (again!.Tokens, again.FrequencyWindows, again.More));

        // Silinen (çok eski) bağlantı artık geçersizdir; yakın zamanda süresi dolan bağlantı hâlâ görüntülenir ve çıkış kabul edilir.
        using var client = host.CreateClient();
        var gone = await client.PostAsJsonAsync($"/api/public/communication/{tenant.Id:N}/view", new PublicCommunicationTokenRequest(seeded.Ancient));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        var kept = await client.PostAsJsonAsync($"/api/public/communication/{tenant.Id:N}/unsubscribe", new PublicCommunicationTokenRequest(seeded.RecentlyExpired));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task Cleaning_one_tenant_never_touches_another_tenants_rows()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        await SeedAsync(a.Id);
        var other = await SeedAsync(b.Id);

        await Runner.RunTenantAsync(a.Id, CancellationToken.None);

        using var scope = TenantScope(b.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(3, await db.CommunicationLinkTokens.AsNoTracking().CountAsync());
        Assert.Equal(3, await db.FrequencyWindows.AsNoTracking().CountAsync());
        Assert.Contains(LinkTokens.Hash(other.Ancient), await db.CommunicationLinkTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync());
    }

    [Fact]
    public async Task Cleanup_has_no_http_surface_and_the_retention_rules_are_what_the_docs_say()
    {
        var tenant = await factory.CreateTenantAsync();
        using var client = host.CreateClient();

        // Temizlik yalnızca arka plan işidir: açık ya da yetkili bir uç nokta yoktur.
        foreach (var path in new[] { "/api/communication/cleanup", "/api/public/communication/cleanup", $"/api/public/communication/{tenant.Id:N}/cleanup" })
        {
            var response = await client.PostAsync(path, null);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.MethodNotAllowed, $"{path}: {response.StatusCode}");
        }

        // Saklama süreleri belgelenmiş değerlerdir: belirteç ömrü (400 gün) + 365 gün; sıklık penceresi 7 gün.
        Assert.Equal(365, CommunicationCleanup.TokenGraceDays);
        Assert.Equal(7, CommunicationCleanup.FrequencyWindowDays);
        Assert.Equal(400, LinkTokens.ValidDays);
    }

    /// <summary>Üç belirteç (çok eski, yeni süresi dolmuş, taze) ve üç sıklık penceresi (30 gün önce, 3 gün önce, bugün).</summary>
    private async Task<(string Ancient, string RecentlyExpired, string Fresh)> SeedAsync(Guid tenantId)
    {
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tokens = (Ancient: LinkTokens.New(), RecentlyExpired: LinkTokens.New(), Fresh: LinkTokens.New());

        // Ömür 400 gündür: 1000 gün önce üretilen belirteç ~600 gün önce, 420 gün önce üretilen 20 gün önce dolmuştur.
        db.CommunicationLinkTokens.Add(new CommunicationLinkToken(LinkTokens.Hash(tokens.Ancient), CommunicationChannel.Email, $"eski-{Guid.NewGuid():N}@x.test", "ref", now.AddDays(-1000)));
        db.CommunicationLinkTokens.Add(new CommunicationLinkToken(LinkTokens.Hash(tokens.RecentlyExpired), CommunicationChannel.Email, $"yeni-dolmus-{Guid.NewGuid():N}@x.test", "ref", now.AddDays(-420)));
        db.CommunicationLinkTokens.Add(new CommunicationLinkToken(LinkTokens.Hash(tokens.Fresh), CommunicationChannel.Email, $"taze-{Guid.NewGuid():N}@x.test", "ref", now));
        await db.SaveChangesAsync();

        var limiter = scope.ServiceProvider.GetRequiredService<IFrequencyLimiter>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var offset in new[] { -30, -3, 0 })
        {
            Assert.True(await limiter.TryReserveAsync(CommunicationChannel.Email, $"pencere-{Guid.NewGuid():N}@x.test", today.AddDays(offset), 5, CancellationToken.None));
        }

        return tokens;
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.Empty, Guid.Empty);
        return scope;
    }
}
