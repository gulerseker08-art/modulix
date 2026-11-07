using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Platform;

/// <summary>
/// İmzalı lisans dosyasını firmaya uygular (ADR-0028 Dilim 7b). Lisans ayrı bir hak kaynağı değildir: firmanın lisans planı
/// (<c>lisans-{kısa ad}</c>) dosyadaki modül ve limitlerle kurulup <see cref="IPlanAdministration.AssignPlanAsync"/> ile atanır; böylece
/// hak çözümü, ek süre/salt okunur evreleri ve "silinmez" kuralı Dilim 3-4'teki gibi çalışır. İşlemi sunucuya erişimi olan kişi
/// komut satırından yapar (<c>license-apply</c>); işlem bu yüzden <see cref="SystemActor"/> adına denetlenir.
/// </summary>
internal sealed class LicenseApplier(ModulixDbContext db, IPlanAdministration plans, TimeProvider time) : ILicenseApplier
{
    /// <summary>Komut satırından yapılan işlemlerin denetim kaydındaki işleyen kimliği (kişi hesabı değildir).</summary>
    public static readonly Guid SystemActor = Guid.Empty;

    public async Task<LicenseApplyResult> ApplyAsync(string licenseText, string publicKey, CancellationToken cancellationToken)
    {
        var payload = LicenseFile.Verify(licenseText, publicKey);
        var now = time.GetUtcNow();
        if (payload.ValidUntil <= now)
        {
            throw new LicenseException("Lisansın süresi dolmuş; yeni lisans dosyası gerekir.");
        }

        var slug = payload.TenantSlug.Trim().ToLowerInvariant();
        var tenantId = await db.Tenants.AsNoTracking().Where(t => t.Slug == slug).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new LicenseException($"Lisanstaki firma bu kurulumda yok: {slug}.");

        var planKey = LicenseFile.PlanKeyFor(slug);
        var input = new PlanInput(planKey, $"Lisans: {payload.Customer.Trim()}", [.. payload.Modules], payload.Limits, IsActive: true);
        try
        {
            var existing = (await plans.ListPlansAsync(cancellationToken)).FirstOrDefault(p => p.Key == planKey);
            if (existing is null)
            {
                await plans.CreatePlanAsync(input, SystemActor, cancellationToken);
            }
            else
            {
                await plans.UpdatePlanAsync(existing.Id, input, SystemActor, cancellationToken);
            }

            var reason = $"Lisans dosyası {payload.LicenseId.Trim()}";
            await plans.AssignPlanAsync(tenantId, planKey, payload.ValidUntil, reason, SystemActor, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new LicenseException($"Lisans uygulanamadı: {ex.Message}");
        }

        db.PlatformAuditLog.Add(new PlatformAuditEntry(
            SystemActor,
            PlatformAuditActions.LicenseApplied,
            tenantId,
            $"Lisans dosyası {payload.LicenseId.Trim()}",
            new JsonObject
            {
                ["licenseId"] = payload.LicenseId.Trim(),
                ["issuedAt"] = payload.IssuedAt.ToString("O"),
                ["validUntil"] = payload.ValidUntil.ToString("O"),
                ["modules"] = new JsonArray([.. payload.Modules.Select(m => JsonValue.Create(m))]),
            }.ToJsonString(),
            Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);

        return new LicenseApplyResult(payload.LicenseId.Trim(), slug, planKey, payload.ValidUntil, [.. payload.Modules]);
    }
}
