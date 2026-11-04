using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Platform;

/// <summary>Limitlerin çözümü: geçerli istisna, yoksa planın değeri, yoksa sınırsız. Kullanım platform tablolarından sayılır.</summary>
internal sealed class EntitlementResolver(ModulixDbContext db, TimeProvider time) : IEntitlementResolver
{
    public async Task<IReadOnlyList<ResolvedLimit>> GetLimitsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var planLimits = await (
            from subscription in db.TenantSubscriptions.AsNoTracking()
            where subscription.TenantId == tenantId && subscription.EndedAt == null
            join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
            from limit in plan.Limits
            select new { limit.LimitKey, limit.Value })
            .ToDictionaryAsync(l => l.LimitKey, l => l.Value, cancellationToken);
        var overrides = await db.LimitOverrides.AsNoTracking().Where(o => o.TenantId == tenantId && o.RevokedAt == null).ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        return [.. LimitKeys.All.Select(l => LimitResolution.Resolve(l.Key, planLimits.TryGetValue(l.Key, out var value) ? value : null, overrides, now))];
    }

    public async Task<ResolvedLimit> GetLimitAsync(Guid tenantId, string limitKey, CancellationToken cancellationToken) =>
        (await GetLimitsAsync(tenantId, cancellationToken)).FirstOrDefault(l => l.Key == limitKey)
        ?? throw new ArgumentException($"Bilinmeyen limit: '{limitKey}'.", nameof(limitKey));

    public Task<int> GetUsageAsync(Guid tenantId, string limitKey, CancellationToken cancellationToken) => limitKey switch
    {
        // Koltuk: etkin ve davet bekleyen üyelikler (davet bir koltuğu ayırır); devre dışı olanlar saymaz.
        LimitKeys.Seats => db.Memberships.AsNoTracking().CountAsync(
            m => m.TenantId == tenantId && (m.Status == MembershipStatus.Active || m.Status == MembershipStatus.Invited), cancellationToken),
        _ => throw new ArgumentException($"Bilinmeyen limit: '{limitKey}'.", nameof(limitKey)),
    };
}

internal sealed class PlanAdministration(
    ModulixDbContext db,
    ModuleCatalog catalog,
    IPlatformAdministration platform,
    IEntitlementResolver resolver,
    TimeProvider time) : IPlanAdministration
{
    public async Task<IReadOnlyList<PlanInfo>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await db.Plans.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var subscribers = await db.TenantSubscriptions.AsNoTracking()
            .Where(s => s.EndedAt == null)
            .GroupBy(s => s.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.PlanId, g => g.Count, cancellationToken);
        return [.. plans.Select(p => ToInfo(p, subscribers.GetValueOrDefault(p.Id)))];
    }

    public async Task<PlanInfo> CreatePlanAsync(PlanInput input, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = new Plan(input.Key, input.Name, ValidateModules(input.Modules), input.Limits);
        if (await db.Plans.AnyAsync(p => p.Key == plan.Key, cancellationToken))
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, $"'{plan.Key}' anahtarlı plan zaten var.");
        }

        db.Plans.Add(plan);
        Audit(actingUserId, PlatformAuditActions.PlanCreated, null, null, Describe(plan));
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(plan, 0);
    }

    public async Task<PlanInfo> UpdatePlanAsync(Guid planId, PlanInput input, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Plan bulunamadı.");
        var before = Describe(plan);
        plan.Update(input.Name, ValidateModules(input.Modules), input.Limits, input.IsActive);
        Audit(actingUserId, PlatformAuditActions.PlanUpdated, null, null, new JsonObject { ["before"] = before, ["after"] = Describe(plan) });
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.TenantSubscriptions.CountAsync(s => s.PlanId == plan.Id && s.EndedAt == null, cancellationToken);
        return ToInfo(plan, count);
    }

    public async Task<TenantPlanOverview> GetTenantPlanAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await EnsureTenantAsync(tenantId, cancellationToken);
        var subscription = await (
            from s in db.TenantSubscriptions.AsNoTracking()
            where s.TenantId == tenantId && s.EndedAt == null
            join p in db.Plans.AsNoTracking() on s.PlanId equals p.Id
            select new SubscriptionInfo(p.Id, p.Key, p.Name, s.StartsAt, s.ValidUntil))
            .SingleOrDefaultAsync(cancellationToken);

        var limits = new List<TenantLimitStatus>();
        foreach (var resolved in await resolver.GetLimitsAsync(tenantId, cancellationToken))
        {
            limits.Add(new TenantLimitStatus(
                resolved.Key, LimitKeys.NameOf(resolved.Key), resolved.Value, resolved.Source, resolved.OverrideUntil,
                await resolver.GetUsageAsync(tenantId, resolved.Key, cancellationToken)));
        }

        return new TenantPlanOverview(subscription, limits);
    }

    public async Task AssignPlanAsync(
        Guid tenantId, string planKey, DateTimeOffset? validUntil, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        var trimmedReason = Required(reason, "reason", "Plan atamak için gerekçe zorunludur.");
        var now = time.GetUtcNow();
        if (validUntil is { } until && until <= now)
        {
            throw new ArgumentException("Bitiş tarihi gelecekte olmalı.", "validUntil");
        }

        await EnsureTenantAsync(tenantId, cancellationToken);
        var key = (planKey ?? string.Empty).Trim().ToLowerInvariant();
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Key == key, cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Plan bulunamadı.");
        if (!plan.IsActive)
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Etkin olmayan plan atanamaz.");
        }

        // Modül hakları plana göre ayarlanır (her biri kendi platform kaydı ve firma denetimiyle). Elle ya da istisna olarak verilmiş
        // haklara dokunulmaz; önceki plandan gelip yeni planda olmayan modüller kapatılır ama veri silinmez.
        var current = await db.ModuleEntitlements.AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(cancellationToken);
        var planModules = plan.Modules.Select(m => m.ModuleKey).ToHashSet(StringComparer.Ordinal);
        var terms = new EntitlementTerms(EntitlementSource.Plan, validUntil, $"Plan: {plan.Name}");
        var changed = new JsonArray();
        foreach (var module in catalog.Modules.Select(m => m.Key).Where(planModules.Contains))
        {
            var existing = current.FirstOrDefault(e => e.ModuleKey == module);
            if (existing is { IsEnabled: true, Source: not EntitlementSource.Plan })
            {
                continue;
            }

            await platform.SetModuleAsync(tenantId, module, true, actingUserId, cancellationToken, terms, trimmedReason);
            changed.Add($"+{module}");
        }

        foreach (var stale in current.Where(e => e is { IsEnabled: true, Source: EntitlementSource.Plan } && !planModules.Contains(e.ModuleKey)))
        {
            await platform.SetModuleAsync(tenantId, stale.ModuleKey, false, actingUserId, cancellationToken, reason: trimmedReason);
            changed.Add($"-{stale.ModuleKey}");
        }

        var previous = await db.TenantSubscriptions.Where(s => s.TenantId == tenantId && s.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var subscription in previous)
        {
            subscription.End(now);
        }

        // Eski aboneliğin kapanışı ile yenisinin açılışı aynı SaveChanges'te yazılır (firma başına tek geçerli kayıt).
        db.TenantSubscriptions.Add(new TenantSubscription(tenantId, plan.Id, now, validUntil, trimmedReason));
        Audit(actingUserId, PlatformAuditActions.PlanAssigned, tenantId, trimmedReason, new JsonObject
        {
            ["plan"] = plan.Key,
            ["validUntil"] = validUntil?.ToString("O"),
            ["modules"] = changed,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetLimitOverrideAsync(
        Guid tenantId, string limitKey, int value, DateTimeOffset validUntil, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        await EnsureTenantAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        var created = new LimitOverride(tenantId, limitKey, value, validUntil, reason, now);

        foreach (var active in await db.LimitOverrides.Where(o => o.TenantId == tenantId && o.LimitKey == limitKey && o.RevokedAt == null).ToListAsync(cancellationToken))
        {
            active.Revoke(now);
        }

        db.LimitOverrides.Add(created);
        Audit(actingUserId, PlatformAuditActions.LimitOverridden, tenantId, created.Reason,
            new JsonObject { ["limit"] = limitKey, ["value"] = value, ["validUntil"] = validUntil.ToString("O") });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeLimitOverrideAsync(Guid tenantId, string limitKey, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        var trimmedReason = Required(reason, "reason", "İstisnayı geri almak için gerekçe zorunludur.");
        await EnsureTenantAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        var active = (await db.LimitOverrides.Where(o => o.TenantId == tenantId && o.LimitKey == limitKey && o.RevokedAt == null).ToListAsync(cancellationToken))
            .Where(o => o.IsActiveAt(now)).ToList();
        if (active.Count == 0)
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Geri alınacak geçerli istisna yok.");
        }

        foreach (var item in active)
        {
            item.Revoke(now);
        }

        Audit(actingUserId, PlatformAuditActions.LimitRevoked, tenantId, trimmedReason, new JsonObject { ["limit"] = limitKey });
        await db.SaveChangesAsync(cancellationToken);
    }

    private IReadOnlyList<string> ValidateModules(IReadOnlyCollection<string> modules)
    {
        var requested = (modules ?? []).ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(m => !catalog.ContainsModule(m) || ModuleCatalog.IsAlwaysEnabled(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Plana eklenemeyen modül: {string.Join(", ", unknown)}.", "modules");
        }

        return [.. catalog.Modules.Select(m => m.Key).Where(requested.Contains)];
    }

    private async Task EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw new UserAdministrationException(UserAdministrationError.NotFound, "Firma bulunamadı.");
        }
    }

    private void Audit(Guid actingUserId, string action, Guid? tenantId, string? reason, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, tenantId, reason, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private static JsonObject Describe(Plan plan) => new()
    {
        ["key"] = plan.Key,
        ["name"] = plan.Name,
        ["isActive"] = plan.IsActive,
        ["modules"] = new JsonArray([.. plan.Modules.Select(m => m.ModuleKey).Order(StringComparer.Ordinal).Select(k => JsonValue.Create(k))]),
        ["limits"] = new JsonObject(plan.Limits.OrderBy(l => l.LimitKey, StringComparer.Ordinal).Select(l => KeyValuePair.Create(l.LimitKey, (JsonNode?)JsonValue.Create(l.Value)))),
    };

    private PlanInfo ToInfo(Plan plan, int subscribers) => new(
        plan.Id,
        plan.Key,
        plan.Name,
        plan.IsActive,
        [.. catalog.Modules.Select(m => m.Key).Where(k => plan.Modules.Any(m => m.ModuleKey == k))],
        plan.Limits.ToDictionary(l => l.LimitKey, l => l.Value),
        subscribers);

    private static string Required(string? value, string field, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message, field) : value.Trim();
}
