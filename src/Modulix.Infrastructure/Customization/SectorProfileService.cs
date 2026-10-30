using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customization;

/// <summary>
/// Sektör profilleri (Y9c). Alanlar ve liste değerleri kendi servisleriyle eklenir (doğrulama ve denetim kaydı onlarındır);
/// profilin kendisi ve açılan modüller <c>core.tenant</c> kaydı olarak denetime yazılır. Hepsi tek işlemdir.
/// </summary>
internal sealed class SectorProfileService(
    ModulixDbContext db,
    PlatformDbContext platform,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    SectorProfileCatalog profiles,
    IModuleEntitlements entitlements,
    ICustomFieldService fields,
    IChoiceListService lists,
    IChoiceLists choiceLists,
    IAuditWriter audit) : ISectorProfileService
{
    public async Task<TenantProfile> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant().TenantId;
        return await platform.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantProfile(t.ProfileKey, t.ProfileAppliedAt))
            .SingleAsync(cancellationToken);
    }

    public async Task<ProfilePlan?> PreviewAsync(string profileKey, bool enableModules, CancellationToken cancellationToken) =>
        profiles.Find(profileKey) is { } profile ? await PlanAsync(profile, enableModules, cancellationToken) : null;

    public async Task<ProfilePlan?> ApplyAsync(string profileKey, bool enableModules, CancellationToken cancellationToken)
    {
        if (profiles.Find(profileKey) is not { } profile)
        {
            return null;
        }

        var tenant = RequireTenant();
        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var plan = await PlanAsync(profile, enableModules, cancellationToken);

            // Tenant profil anahtarı platform bağlamındadır (ADR-0020 Dilim 1); tenant işi (özel alan/liste/audit) tenant bağlamında.
            var row = await platform.Tenants.SingleAsync(t => t.Id == tenant.TenantId, cancellationToken);
            var modulesBefore = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
            var before = Snapshot(row.ProfileKey, modulesBefore);

            foreach (var module in plan.ModulesToEnable)
            {
                await entitlements.SetEnabledAsync(tenant.TenantId, module, enabled: true, cancellationToken);
            }

            foreach (var field in plan.Fields)
            {
                var options = field.Options?.Select(o => new CustomFieldOptionInput(null, o)).ToList();
                await fields.CreateAsync(field.EntityKey, new CustomFieldInput(field.Label, field.Type, field.Required, field.ShowInList, options), cancellationToken);
            }

            if (plan.ListValues.Count > 0)
            {
                var current = await lists.ListAsync(cancellationToken);
                foreach (var group in plan.ListValues.GroupBy(v => v.ListKey))
                {
                    var list = current.Single(l => l.Definition.Key == group.Key).List;
                    _ = await lists.SaveAsync(group.Key, SectorProfileRules.Extend(list, group), cancellationToken)
                        ?? throw new InvalidOperationException($"Liste kaydedilemedi: '{group.Key}'.");
                }
            }

            row.ApplyProfile(profile.Key);
            var after = Snapshot(row.ProfileKey, plan.ModulesToEnable.Count == 0 ? modulesBefore : modulesBefore.Concat(plan.ModulesToEnable));
            if (!JsonNode.DeepEquals(before, after))
            {
                audit.Add(new AuditEntry(
                    tenant.TenantId,
                    tenant.UserId,
                    CoreModule.AuditedEntityKeys.Tenant,
                    tenant.TenantId,
                    AuditAction.Updated,
                    RecordDiff.Compute(before, after),
                    ExecutionSource.Api,
                    Activity.Current?.TraceId.ToString()));
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            // Tenant işi başarıyla bittikten sonra platform bağlamında profil anahtarını kaydet (ayrı bağlam,
            // ayrı işlem; ADR-0020 Dilim 1: platform/tenant tek atomik işlem varsayılmaz).
            await platform.SaveChangesAsync(cancellationToken);

            return plan;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<ProfilePlan> PlanAsync(SectorProfile profile, bool enableModules, CancellationToken cancellationToken)
    {
        var enabled = await entitlements.GetEnabledAsync(RequireTenant().TenantId, cancellationToken);
        var labels = (await fields.ListAsync(null, includeArchived: true, cancellationToken))
            .GroupBy(f => f.EntityKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(f => f.Label)]);
        // Kapalı modülün listesi de okunur (koddaki varsayılan): açılacak modülde hazır değerle aynı adlı değer eklenmez.
        var current = new Dictionary<string, ChoiceList>();
        foreach (var key in profile.ListValues.Select(v => v.ListKey).Distinct())
        {
            current[key] = await choiceLists.GetAsync(key, cancellationToken);
        }

        return SectorProfileRules.Plan(profile, catalog, enabled, enableModules, labels, current);
    }

    // Profil adıyla yazılır; modüller katalog sırasıyla (platformun modül değişikliği kaydıyla aynı biçim).
    private JsonObject Snapshot(string? profileKey, IEnumerable<string> modules)
    {
        var set = modules.ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["profile"] = profileKey is null ? null : profiles.Find(profileKey)?.Name ?? profileKey,
            ["modules"] = new JsonArray([.. catalog.Modules.Select(m => m.Key).Where(k => set.Contains(k) && !ModuleCatalog.IsAlwaysEnabled(k)).Select(k => JsonValue.Create(k))]),
        };
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan sektör profili uygulanamaz.");
}
