using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Automation;
using Modulix.Core.FieldRules;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.FieldRules;

/// <summary>
/// Platform kural paketleri (ADR-0029 K8). Paket tabloları <c>platform</c> şemasındadır ve RLS dışıdır. Yayın ve geri alma tek işlemdedir;
/// her yazma <c>PlatformAuditLog</c>'a yazılır. Platformda tenant olmadığı için kuralların alanları yalnızca modül alanlarıyla doğrulanır
/// (tenant'ın özel alanları paket kuralında kullanılamaz; tenant uyarlamasında kullanılabilir).
/// </summary>
internal sealed class RulePackageService(ModulixDbContext db, ModuleCatalog modules) : IRulePackageService
{
    public async Task<IReadOnlyList<RulePackageView>> ListAsync(CancellationToken cancellationToken)
    {
        var packages = await db.RulePackages.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken);
        var versions = (await db.RulePackageVersions.AsNoTracking().Where(v => v.Status != RulePackageStatus.Superseded).ToListAsync(cancellationToken))
            .ToLookup(v => v.PackageId);
        return [.. packages.Select(p => ToView(p, versions[p.Id]))];
    }

    public async Task<RulePackageView> CreateAsync(
        Guid actingUserId, string code, string name, RuleLayerKind layer, Guid? nodeId, CancellationToken cancellationToken)
    {
        var package = new RulePackage(code, name, layer, nodeId);
        if (await db.RulePackages.AnyAsync(p => p.Code == code, cancellationToken))
        {
            throw new RulePackageConflictException($"Bu kodla bir paket zaten var: '{code}'.");
        }

        if (nodeId is { } node && !await db.ActivityNodes.AnyAsync(n => n.Id == node, cancellationToken))
        {
            throw new ArgumentException("Bağlanacak faaliyet katalogda yok.", nameof(nodeId));
        }

        db.RulePackages.Add(package);
        AuditPlatform(actingUserId, "rules.package.created", new JsonObject { ["code"] = package.Code, ["layer"] = package.Layer.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return ToView(package, []);
    }

    public async Task<RulePackageView> SaveDraftAsync(
        Guid actingUserId, Guid packageId, IReadOnlyList<FieldRule> rules, string? note, long? expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var package = await FindAsync(packageId, cancellationToken);
        var issues = await ValidateAsync(rules);
        if (issues.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", issues.Select(i => $"{i.Path}: {i.Message}")), nameof(rules));
        }

        var json = FieldRuleJson.Serialize(rules);
        var draft = await db.RulePackageVersions.SingleOrDefaultAsync(v => v.PackageId == packageId && v.Status == RulePackageStatus.Draft, cancellationToken);
        if (draft is null)
        {
            var next = (await db.RulePackageVersions.Where(v => v.PackageId == packageId).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
            draft = new RulePackageVersion(packageId, next, json, note);
            db.RulePackageVersions.Add(draft);
        }
        else
        {
            if (expectedRevision != draft.Revision)
            {
                throw new RulePackageConflictException("Taslak siz düzenlerken değişti; yenileyip tekrar deneyin.");
            }

            db.Entry(draft).Property(v => v.Revision).OriginalValue = expectedRevision.Value;
            draft.ReplaceRules(json, note);
        }

        AuditPlatform(actingUserId, "rules.package.draft-saved", new JsonObject { ["code"] = package.Code, ["version"] = draft.Number, ["rules"] = rules.Count });
        await SaveAsync(cancellationToken);
        return await ViewAsync(package, cancellationToken);
    }

    public async Task<RulePublishPreview> PreviewPublishAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await FindAsync(packageId, cancellationToken);
        var draft = await db.RulePackageVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.PackageId == packageId && v.Status == RulePackageStatus.Draft, cancellationToken)
            ?? throw new RulePackageNotFoundException("Yayınlanacak taslak yok.");
        return await CheckAsync(package, FieldRuleJson.Deserialize(draft.RulesJson), cancellationToken);
    }

    public async Task<RulePackageView> PublishAsync(Guid actingUserId, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await FindAsync(packageId, cancellationToken);
        var draft = await db.RulePackageVersions.SingleOrDefaultAsync(v => v.PackageId == packageId && v.Status == RulePackageStatus.Draft, cancellationToken)
            ?? throw new RulePackageNotFoundException("Yayınlanacak taslak yok.");
        var preview = await CheckAsync(package, FieldRuleJson.Deserialize(draft.RulesJson), cancellationToken);
        if (!preview.CanPublish)
        {
            throw new RulePackageConflictException(Describe(preview));
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await SupersedeCurrentAsync(packageId, cancellationToken);
        draft.Publish(actingUserId);
        AuditPlatform(actingUserId, "rules.package.published", new JsonObject
        {
            ["code"] = package.Code,
            ["version"] = draft.Number,
            ["added"] = preview.Added.Count,
            ["removed"] = preview.Removed.Count,
            ["changed"] = preview.Changed.Count,
        });
        await SaveAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await ViewAsync(package, cancellationToken);
    }

    public async Task<RulePackageView> RollbackAsync(Guid actingUserId, Guid packageId, int toNumber, CancellationToken cancellationToken)
    {
        var package = await FindAsync(packageId, cancellationToken);
        var versions = await db.RulePackageVersions.Where(v => v.PackageId == packageId).ToListAsync(cancellationToken);
        var target = versions.SingleOrDefault(v => v.Number == toNumber && v.Status != RulePackageStatus.Draft)
            ?? throw new RulePackageNotFoundException("Geri alınacak yayınlanmış sürüm bulunamadı.");
        if (target.Status == RulePackageStatus.Published)
        {
            throw new ArgumentException("Bu sürüm zaten yayında.", nameof(toNumber));
        }

        if (versions.Any(v => v.Status == RulePackageStatus.Draft))
        {
            throw new RulePackageConflictException("Açık bir taslak var; önce yayınlayın ya da silin.");
        }

        var rules = FieldRuleJson.Deserialize(target.RulesJson);
        var preview = await CheckAsync(package, rules, cancellationToken);
        if (!preview.CanPublish)
        {
            throw new RulePackageConflictException(Describe(preview));
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await SupersedeCurrentAsync(packageId, cancellationToken);
        var restored = new RulePackageVersion(packageId, versions.Max(v => v.Number) + 1, target.RulesJson, $"Sürüm {target.Number} geri alındı.");
        db.RulePackageVersions.Add(restored);
        await SaveAsync(cancellationToken);
        restored.Publish(actingUserId);
        AuditPlatform(actingUserId, "rules.package.rolled-back", new JsonObject { ["code"] = package.Code, ["from"] = target.Number, ["version"] = restored.Number });
        await SaveAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await ViewAsync(package, cancellationToken);
    }

    public async Task<RulePackageView> DiscardDraftAsync(Guid actingUserId, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await FindAsync(packageId, cancellationToken);
        var draft = await db.RulePackageVersions.SingleOrDefaultAsync(v => v.PackageId == packageId && v.Status == RulePackageStatus.Draft, cancellationToken)
            ?? throw new RulePackageNotFoundException("Silinecek taslak yok.");
        db.RulePackageVersions.Remove(draft);
        AuditPlatform(actingUserId, "rules.package.draft-discarded", new JsonObject { ["code"] = package.Code, ["version"] = draft.Number });
        await SaveAsync(cancellationToken);
        return await ViewAsync(package, cancellationToken);
    }

    private async Task<RulePublishPreview> CheckAsync(RulePackage package, IReadOnlyList<FieldRule> rules, CancellationToken cancellationToken)
    {
        var issues = await ValidateAsync(rules);

        // Yayındaki diğer paketler: aynı katmanda kural anahtarı paketler arasında tekil olmalıdır.
        var others = await (from v in db.RulePackageVersions.AsNoTracking()
                            join p in db.RulePackages.AsNoTracking() on v.PackageId equals p.Id
                            where v.Status == RulePackageStatus.Published && p.Id != package.Id
                            select new { p.Layer, v.RulesJson }).ToListAsync(cancellationToken);
        var productOthers = others.Where(o => o.Layer == RuleLayerKind.ProductStandard).SelectMany(o => FieldRuleJson.Deserialize(o.RulesJson)).ToList();
        var sectorOthers = others.Where(o => o.Layer == RuleLayerKind.SectorPackage).SelectMany(o => FieldRuleJson.Deserialize(o.RulesJson)).ToList();

        var sameLayerKeys = (package.Layer == RuleLayerKind.ProductStandard ? productOthers : sectorOthers).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var conflicts = rules.Where(r => sameLayerKeys.Contains(r.Key))
            .Select(r => new RuleMergeIssue(r.Key, $"Kural anahtarı başka bir yayınlanmış pakette de var: {r.Key}."))
            .ToList();

        var layers = new List<RuleLayer>();
        if (package.Layer == RuleLayerKind.ProductStandard)
        {
            layers.Add(new RuleLayer(RuleLayerKind.ProductStandard, [.. productOthers, .. rules]));
        }
        else
        {
            layers.Add(new RuleLayer(RuleLayerKind.ProductStandard, productOthers));
            layers.Add(new RuleLayer(RuleLayerKind.SectorPackage, rules));
        }

        var merged = RuleLayerMerger.Merge(layers);
        conflicts.AddRange(merged.Conflicts);

        var published = await (from v in db.RulePackageVersions.AsNoTracking()
                               where v.PackageId == package.Id && v.Status == RulePackageStatus.Published
                               select v.RulesJson).SingleOrDefaultAsync(cancellationToken);
        var before = (published is null ? [] : FieldRuleJson.Deserialize(published)).ToDictionary(r => r.Key, r => FieldRuleJson.Serialize([r]), StringComparer.Ordinal);
        var after = rules.GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => FieldRuleJson.Serialize([g.First()]), StringComparer.Ordinal);

        return new RulePublishPreview(
            issues,
            conflicts,
            merged.Warnings,
            [.. after.Keys.Except(before.Keys).Order(StringComparer.Ordinal)],
            [.. before.Keys.Except(after.Keys).Order(StringComparer.Ordinal)],
            [.. after.Keys.Intersect(before.Keys).Where(k => before[k] != after[k]).Order(StringComparer.Ordinal)]);
    }

    private Task<IReadOnlyList<AutomationIssue>> ValidateAsync(IReadOnlyList<FieldRule> rules) =>
        FieldRuleChecker.ValidateAsync(rules, modules, entityKey => Task.FromResult<AutomationFieldSet?>(
            modules.FindAutomationEntity(entityKey) is null ? null : FieldRuleChecker.ModuleFieldSet(modules, entityKey)));

    private async Task SupersedeCurrentAsync(Guid packageId, CancellationToken cancellationToken)
    {
        // Filtreli benzersiz dizin yüzünden eski sürüm önce ayrı bir kayıtla geçersiz kılınır (aynı işlem).
        var current = await db.RulePackageVersions.SingleOrDefaultAsync(v => v.PackageId == packageId && v.Status == RulePackageStatus.Published, cancellationToken);
        if (current is not null)
        {
            current.Supersede();
            await SaveAsync(cancellationToken);
        }
    }

    private async Task<RulePackage> FindAsync(Guid packageId, CancellationToken cancellationToken) =>
        await db.RulePackages.SingleOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            ?? throw new RulePackageNotFoundException("Paket bulunamadı.");

    private async Task<RulePackageView> ViewAsync(RulePackage package, CancellationToken cancellationToken) =>
        ToView(package, await db.RulePackageVersions.AsNoTracking()
            .Where(v => v.PackageId == package.Id && v.Status != RulePackageStatus.Superseded).ToListAsync(cancellationToken));

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new RulePackageConflictException("Paket siz çalışırken başkası tarafından değiştirildi; yenileyip tekrar deneyin.");
        }
        catch (DbUpdateException)
        {
            throw new RulePackageConflictException("Paket başka bir yönetici tarafından değiştirildi; yenileyip tekrar deneyin.");
        }
    }

    private void AuditPlatform(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private static string Describe(RulePublishPreview preview) =>
        string.Join(" ", preview.Issues.Select(i => $"{i.Path}: {i.Message}").Concat(preview.Conflicts.Select(c => c.Message)));

    private static RulePackageView ToView(RulePackage package, IEnumerable<RulePackageVersion> versions)
    {
        static RulePackageVersionView? Of(RulePackageVersion? v) =>
            v is null ? null : new RulePackageVersionView(v.Number, v.Status, FieldRuleJson.Deserialize(v.RulesJson), v.Note, v.Revision, v.PublishedAt);
        var list = versions.ToList();
        return new RulePackageView(
            package.Id, package.Code, package.Name, package.Layer, package.NodeId,
            Of(list.SingleOrDefault(v => v.Status == RulePackageStatus.Published)),
            Of(list.SingleOrDefault(v => v.Status == RulePackageStatus.Draft)));
    }
}
