using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 7b: imzalı lisans dosyası firmaya uygulanır. Hak, mevcut plan/abonelik modeline yazılır (lisans planı);
/// yeni lisans eskisinin yerine geçer ama hiçbir kayıt silinmez. Başka firmanın lisansı ve yanlış anahtarla imzalı dosya uygulanamaz.
/// Uygulama yalnızca komut satırındandır (HTTP ucu yok), bu yüzden yetki sınaması imzanın özgünlüğüdür.
/// </summary>
public class LicenseApplierTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly (string PrivateKey, string PublicKey) Keys = LicenseFile.GenerateKeyPair();

    private static string Sign(string slug, string[] modules, DateTimeOffset validUntil, int? seats = null, string? privateKey = null) =>
        LicenseFile.Sign(
            new LicensePayload(
                $"LIC-{Guid.NewGuid():N}"[..12], slug, "Test A.Ş.", modules,
                seats is { } s ? new Dictionary<string, int> { ["seats"] = s } : null, DateTimeOffset.UtcNow.AddMinutes(-1), validUntil),
            privateKey ?? Keys.PrivateKey);

    private async Task<LicenseApplyResult> ApplyAsync(string licenseText, string? publicKey = null)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ILicenseApplier>().ApplyAsync(licenseText, publicKey ?? Keys.PublicKey, CancellationToken.None);
    }

    [Fact]
    public async Task Applying_a_license_opens_its_modules_with_its_expiry_and_limits_and_writes_the_audit()
    {
        var tenant = await factory.CreateTenantAsync();
        var until = DateTimeOffset.UtcNow.AddDays(180);

        var result = await ApplyAsync(Sign(tenant.Slug, ["crm"], until, seats: 7));

        Assert.Equal($"lisans-{tenant.Slug}", result.PlanKey);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var crm = Assert.Single(await db.ModuleEntitlements.AsNoTracking().Where(e => e.TenantId == tenant.Id && e.ModuleKey == "crm").ToListAsync());
        Assert.True(crm.IsEnabled);
        Assert.Equal(until.ToUnixTimeSeconds(), crm.ValidUntil!.Value.ToUnixTimeSeconds());
        var limit = await scope.ServiceProvider.GetRequiredService<IEntitlementResolver>().GetLimitAsync(tenant.Id, LimitKeys.Seats, CancellationToken.None);
        Assert.Equal(7, limit.Value);
        Assert.Contains(
            await db.PlatformAuditLog.AsNoTracking().Where(a => a.TargetTenantId == tenant.Id).Select(a => a.Action).ToListAsync(),
            a => a == PlatformAuditActions.LicenseApplied);
    }

    [Fact]
    public async Task A_new_license_replaces_the_old_one_and_deletes_nothing()
    {
        var tenant = await factory.CreateTenantAsync();
        await ApplyAsync(Sign(tenant.Slug, ["crm", "support"], DateTimeOffset.UtcNow.AddDays(30), seats: 5));

        await ApplyAsync(Sign(tenant.Slug, ["support"], DateTimeOffset.UtcNow.AddDays(365), seats: 50));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        // Eski abonelik kapandı ama kayıt duruyor; yeni olan geçerli ve tek.
        var history = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant.Id).OrderBy(s => s.StartsAt).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.NotNull(history[0].EndedAt);
        Assert.Null(history[1].EndedAt);
        // Lisanstan çıkan modül kapandı (kayıt kalır), kalan modül açık; tek lisans planı güncellendi.
        var entitlements = await db.ModuleEntitlements.AsNoTracking().Where(e => e.TenantId == tenant.Id).ToListAsync();
        Assert.False(Assert.Single(entitlements, e => e.ModuleKey == "crm").IsEnabled);
        Assert.True(Assert.Single(entitlements, e => e.ModuleKey == "support").IsEnabled);
        Assert.Single(await db.Plans.AsNoTracking().Where(p => p.Key == $"lisans-{tenant.Slug}").ToListAsync());
        var limit = await scope.ServiceProvider.GetRequiredService<IEntitlementResolver>().GetLimitAsync(tenant.Id, LimitKeys.Seats, CancellationToken.None);
        Assert.Equal(50, limit.Value);
    }

    [Fact]
    public async Task Invalid_licenses_are_rejected_and_change_nothing()
    {
        var tenant = await factory.CreateTenantAsync();
        // Süresi dolmuş, tanınmayan firma, bilinmeyen modül ve bozuk dosya.
        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync(Sign(tenant.Slug, ["crm"], DateTimeOffset.UtcNow.AddMinutes(-5))));
        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync(Sign($"yok-{Guid.NewGuid():N}"[..12], ["crm"], DateTimeOffset.UtcNow.AddDays(30))));
        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync(Sign(tenant.Slug, ["yok-modul"], DateTimeOffset.UtcNow.AddDays(30))));
        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync("{ bozuk"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.TenantSubscriptions.AsNoTracking().AnyAsync(s => s.TenantId == tenant.Id));
        Assert.False(await db.ModuleEntitlements.AsNoTracking().AnyAsync(e => e.TenantId == tenant.Id && e.ModuleKey == "crm"));
    }

    [Fact]
    public async Task A_license_signed_with_the_wrong_key_is_not_applied()
    {
        var tenant = await factory.CreateTenantAsync();
        var (otherPrivateKey, _) = LicenseFile.GenerateKeyPair();

        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync(Sign(tenant.Slug, ["crm"], DateTimeOffset.UtcNow.AddDays(30), privateKey: otherPrivateKey)));
        await Assert.ThrowsAsync<LicenseException>(() => ApplyAsync(Sign(tenant.Slug, ["crm"], DateTimeOffset.UtcNow.AddDays(30)), publicKey: ""));

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().TenantSubscriptions.AsNoTracking().AnyAsync(s => s.TenantId == tenant.Id));
    }

    [Fact]
    public async Task A_license_for_one_tenant_does_not_open_modules_for_another()
    {
        var licensed = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();

        await ApplyAsync(Sign(licensed.Slug, ["crm"], DateTimeOffset.UtcNow.AddDays(30), seats: 3));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.TenantSubscriptions.AsNoTracking().AnyAsync(s => s.TenantId == other.Id));
        Assert.False(await db.ModuleEntitlements.AsNoTracking().AnyAsync(e => e.TenantId == other.Id && e.ModuleKey == "crm" && e.IsEnabled));
        Assert.Null((await scope.ServiceProvider.GetRequiredService<IEntitlementResolver>().GetLimitAsync(other.Id, LimitKeys.Seats, CancellationToken.None)).Value);
    }
}
