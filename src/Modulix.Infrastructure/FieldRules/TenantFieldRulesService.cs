using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.FieldRules;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.FieldRules;

/// <summary>
/// Tenant bağlamında etkin alan kuralları ve tenant uyarlaması (ADR-0029 K8). Etkin kural = ürün standardı + tenant'ın seçtiği etkin
/// faaliyetlerin ve üst düğümlerinin yayınlanmış sektör paketleri + tenant uyarlaması. Pasif faaliyetin paketi uygulanmaz. Uyarlama
/// <c>core.RuleAdaptations</c>'tadır (RLS + EF süzgeci) ve <c>core.rule-adaptation</c> olarak denetime yazılır.
/// </summary>
internal sealed class TenantFieldRulesService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog modules,
    IAutomationCatalog catalog,
    IAuditWriter audit,
    ILogger<TenantFieldRulesService> logger) : ITenantFieldRules, ITenantRuleSimulation
{
    public async Task<RuleMergeResult> GetEffectiveAsync(CancellationToken cancellationToken)
    {
        _ = RequireTenant();
        var adaptation = await LoadAdaptationAsync(cancellationToken);
        try
        {
            return RuleLayerMerger.Merge(await BuildLayersAsync(adaptation.Rules, adaptation.Disabled, cancellationToken));
        }
        catch (CorruptRulePackageException exception)
        {
            // Yalnızca paket kodu ve sürümü loglanır (kural içeriği yok); etkilenen her yazma reddedilir, bu yüzden hata görünür kalmalı.
            logger.LogError(exception, "Yayınlanmış kural paketi okunamıyor: {PackageCode} sürüm {Version}.", exception.PackageCode, exception.Version);
            throw;
        }
    }

    public async Task<TenantRuleAdaptationView> GetAdaptationAsync(CancellationToken cancellationToken)
    {
        _ = RequireTenant();
        var adaptation = await LoadAdaptationAsync(cancellationToken);
        return new TenantRuleAdaptationView(adaptation.Rules, adaptation.Disabled, adaptation.Revision);
    }

    public async Task<TenantRuleAdaptationView> SaveAdaptationAsync(
        IReadOnlyList<FieldRule> rules, IReadOnlyList<string> disabledRuleKeys, long? expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(disabledRuleKeys);
        var tenant = RequireTenant();

        var issues = await FieldRuleChecker.ValidateAsync(rules, modules, entityKey => catalog.GetFieldSetAsync(entityKey, cancellationToken));
        if (issues.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", issues.Select(i => $"{i.Path}: {i.Message}")), nameof(rules));
        }

        // Çakışma (ör. kilitli kuralı geçersiz kılmak) kaydedilmez; uyarı (kalkmış kuralı kapatmak) kaydı engellemez.
        var merged = RuleLayerMerger.Merge(await BuildLayersAsync(rules, disabledRuleKeys, cancellationToken));
        if (merged.Conflicts.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", merged.Conflicts.Select(c => c.Message)), nameof(rules));
        }

        var row = await db.RuleAdaptations.SingleOrDefaultAsync(cancellationToken);
        var before = Snapshot(row is null ? [] : FieldRuleJson.Deserialize(row.RulesJson), row is null ? [] : FieldRuleJson.DeserializeKeys(row.DisabledKeysJson));
        var after = Snapshot(rules, disabledRuleKeys);
        if (row is null)
        {
            if (expectedRevision is not (null or 0))
            {
                throw new RulePackageConflictException("Uyarlama siz düzenlerken değişti; yenileyip tekrar deneyin.");
            }

            row = new TenantRuleAdaptation(FieldRuleJson.Serialize(rules), FieldRuleJson.Serialize(disabledRuleKeys));
            db.RuleAdaptations.Add(row);
        }
        else
        {
            if (expectedRevision != row.Revision)
            {
                throw new RulePackageConflictException("Uyarlama siz düzenlerken değişti; yenileyip tekrar deneyin.");
            }

            db.Entry(row).Property(a => a.Revision).OriginalValue = expectedRevision.Value;
            row.Replace(FieldRuleJson.Serialize(rules), FieldRuleJson.Serialize(disabledRuleKeys));
        }

        if (!JsonNode.DeepEquals(before, after))
        {
            audit.Add(new AuditEntry(
                tenant.TenantId,
                tenant.UserId,
                CoreModule.AuditedEntityKeys.RuleAdaptation,
                tenant.TenantId,
                AuditAction.Updated,
                RecordDiff.Compute(before, after),
                ExecutionSource.Api,
                Activity.Current?.TraceId.ToString()));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new RulePackageConflictException("Uyarlama siz düzenlerken değişti; yenileyip tekrar deneyin.");
        }

        return new TenantRuleAdaptationView([.. rules], [.. disabledRuleKeys], row.Revision);
    }

    private async Task<List<RuleLayer>> BuildLayersAsync(
        IReadOnlyList<FieldRule> adaptationRules, IReadOnlyList<string> disabled, CancellationToken cancellationToken, IReadOnlyList<FieldRule>? subProfileRules = null)
    {
        var selected = await db.ActivitySelections.AsNoTracking().Select(s => s.NodeId).ToListAsync(cancellationToken);
        var nodes = await db.ActivityNodes.AsNoTracking()
            .Select(n => new ActivityNodeInfo(n.Id, n.Code, n.ParentId, n.Kind, n.Name, n.IsActive, n.Readiness))
            .ToListAsync(cancellationToken);
        var active = nodes.Where(n => n.IsActive).Select(n => n.Id).ToHashSet();
        var applicable = selected.Where(active.Contains)
            .SelectMany(id => ActivityTreeRules.Ancestors(nodes, id).Append(id))
            .ToHashSet();

        var packages = await (from v in db.RulePackageVersions.AsNoTracking()
                              join p in db.RulePackages.AsNoTracking() on v.PackageId equals p.Id
                              where v.Status == RulePackageStatus.Published
                              orderby p.Code
                              select new { p.Code, v.Number, p.Layer, p.NodeId, v.RulesJson }).ToListAsync(cancellationToken);

        List<FieldRule> Of(RuleLayerKind kind, Func<Guid?, bool> applies) =>
            [.. packages.Where(p => p.Layer == kind && applies(p.NodeId)).SelectMany(p => FieldRuleJson.DeserializePackage(p.Code, p.Number, p.RulesJson))];

        return
        [
            new RuleLayer(RuleLayerKind.ProductStandard, Of(RuleLayerKind.ProductStandard, _ => true)),
            new RuleLayer(RuleLayerKind.SectorPackage, Of(RuleLayerKind.SectorPackage, id => id is { } node && applicable.Contains(node))),
            new RuleLayer(RuleLayerKind.SubProfile, subProfileRules ?? await LoadSubProfileRulesAsync(cancellationToken)),
            new RuleLayer(RuleLayerKind.TenantAdaptation, adaptationRules, disabled),
        ];
    }

    public async Task<RuleMergeResult> GetEffectiveWithSubProfileRulesAsync(IReadOnlyList<FieldRule> subProfileRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subProfileRules);
        _ = RequireTenant();
        var adaptation = await LoadAdaptationAsync(cancellationToken);
        return RuleLayerMerger.Merge(await BuildLayersAsync(adaptation.Rules, adaptation.Disabled, cancellationToken, subProfileRules));
    }

    // Yalnızca etkin alt profillerin kuralları (ADR-0041 K4); pasife alınan ya da taslak alt profilin kuralı katmana girmez. Sıra belirleyicidir.
    private async Task<List<FieldRule>> LoadSubProfileRulesAsync(CancellationToken cancellationToken)
    {
        var rows = await (from r in db.SubProfileRules.AsNoTracking()
                          join p in db.SubProfiles.AsNoTracking() on r.SubProfileId equals p.Id
                          where p.Status == SubProfileStatus.Active
                          orderby p.Code, r.SortOrder
                          select new { p.Code, Rule = r }).ToListAsync(cancellationToken);
        return [.. rows.Select(x => x.Rule.ToRule(x.Code))];
    }

    private async Task<(IReadOnlyList<FieldRule> Rules, IReadOnlyList<string> Disabled, long Revision)> LoadAdaptationAsync(CancellationToken cancellationToken)
    {
        var row = await db.RuleAdaptations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? ([], [], 0)
            : (FieldRuleJson.Deserialize(row.RulesJson), FieldRuleJson.DeserializeKeys(row.DisabledKeysJson), row.Revision);
    }

    private static JsonObject Snapshot(IReadOnlyList<FieldRule> rules, IReadOnlyList<string> disabled) => new()
    {
        ["rules"] = new JsonArray([.. rules.Select(r => r.Key).Order(StringComparer.Ordinal).Select(k => JsonValue.Create(k))]),
        ["disabled"] = new JsonArray([.. disabled.Order(StringComparer.Ordinal).Select(k => JsonValue.Create(k))]),
    };

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan alan kuralı uyarlaması yapılamaz.");
}
