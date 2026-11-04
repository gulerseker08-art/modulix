using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Sectors;

/// <summary>
/// Sektör/faaliyet kataloğunun platform tarafı yönetimi (ADR-0029). Katalog tabloları <c>platform</c> şemasındadır, RLS dışıdır ve
/// tenant verisi içermez; her yazma, kataloğu değiştiren yönetici ile birlikte <c>PlatformAuditLog</c>'a aynı işlemde yazılır.
/// </summary>
internal sealed class SectorCatalogService(ModulixDbContext db) : ISectorCatalog
{
    public async Task<SectorCatalogView> GetAsync(CancellationToken cancellationToken)
    {
        var nodes = await db.ActivityNodes.AsNoTracking().OrderBy(n => n.SortOrder).ThenBy(n => n.Name).ToListAsync(cancellationToken);
        var capabilities = await db.Capabilities.AsNoTracking().OrderBy(c => c.Code).ToListAsync(cancellationToken);
        var codes = capabilities.ToDictionary(c => c.Id, c => c.Code);
        var nodeCapabilities = (await db.ActivityNodeCapabilities.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(l => l.NodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(l => codes[l.CapabilityId]).Order(StringComparer.Ordinal)]);
        var dependencies = (await db.CapabilityDependencies.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(d => d.CapabilityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(d => codes[d.DependsOnId]).Order(StringComparer.Ordinal)]);

        return new SectorCatalogView(
            [.. nodes.Select(n => ToView(n, nodeCapabilities.GetValueOrDefault(n.Id) ?? []))],
            [.. capabilities.Select(c => new CapabilityView(c.Id, c.Code, c.Name, c.IsActive, dependencies.GetValueOrDefault(c.Id) ?? []))]);
    }

    public async Task<ActivityNodeView> CreateNodeAsync(Guid actingUserId, ActivityNodeInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!ActivityTreeRules.IsValidCode(input.Code))
        {
            throw new ArgumentException("Kod yalnızca küçük harf, rakam ve '-' içerebilir (en çok 50 karakter).", nameof(input.Code));
        }

        var infos = await LoadInfosAsync(cancellationToken);
        var errors = ActivityTreeRules.ValidateParent(infos, null, input.Kind, input.ParentId);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(input.ParentId));
        }

        if (await db.ActivityNodes.AnyAsync(n => n.Code == input.Code, cancellationToken))
        {
            throw new SectorCatalogConflictException($"Bu kodla bir faaliyet zaten var: '{input.Code}'.");
        }

        var node = new ActivityNode(input.Code, input.ParentId, input.Kind, input.Name, input.Description, input.SortOrder);
        db.ActivityNodes.Add(node);
        AuditPlatform(actingUserId, "sector.node.created", new JsonObject { ["code"] = node.Code, ["kind"] = node.Kind.ToString(), ["name"] = node.Name });
        await db.SaveChangesAsync(cancellationToken);
        return ToView(node, []);
    }

    public async Task<ActivityNodeView> UpdateNodeAsync(Guid actingUserId, Guid nodeId, ActivityNodeUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var node = await FindNodeAsync(nodeId, cancellationToken);
        var before = Snapshot(node);

        // İyimser kilit: istemcinin okuduğu sürüm veritabanındakiyle uyuşmazsa kaydetme çakışma olarak reddedilir.
        db.Entry(node).Property(n => n.Version).OriginalValue = update.Version;
        node.Describe(update.Name, update.Description);
        node.Reorder(update.SortOrder);
        node.SetReadiness(update.Readiness);

        AuditPlatform(actingUserId, "sector.node.updated", new JsonObject { ["code"] = node.Code, ["before"] = before, ["after"] = Snapshot(node) });
        await SaveAsync(cancellationToken);
        return ToView(node, await CapabilityCodesAsync(node.Id, cancellationToken));
    }

    public async Task<ActivityNodeView> SetNodeActiveAsync(Guid actingUserId, Guid nodeId, bool active, CancellationToken cancellationToken)
    {
        var node = await FindNodeAsync(nodeId, cancellationToken);
        var infos = await LoadInfosAsync(cancellationToken);
        if (!active)
        {
            var errors = ActivityTreeRules.ValidateDeactivate(infos, node.Id);
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", errors), nameof(active));
            }
        }
        else if (node.ParentId is { } parentId && infos.Any(n => n.Id == parentId && !n.IsActive))
        {
            throw new ArgumentException("Üst faaliyet pasifken alt faaliyet açılamaz.", nameof(active));
        }

        node.SetActive(active);
        AuditPlatform(actingUserId, active ? "sector.node.activated" : "sector.node.deactivated", new JsonObject { ["code"] = node.Code });
        await SaveAsync(cancellationToken);
        return ToView(node, await CapabilityCodesAsync(node.Id, cancellationToken));
    }

    public async Task<ActivityNodeView> SetNodeCapabilitiesAsync(
        Guid actingUserId, Guid nodeId, IReadOnlyCollection<string> capabilityCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilityCodes);
        var node = await FindNodeAsync(nodeId, cancellationToken);
        var wanted = capabilityCodes.Distinct(StringComparer.Ordinal).ToList();
        var capabilities = await db.Capabilities.Where(c => wanted.Contains(c.Code)).ToListAsync(cancellationToken);
        var unknown = wanted.Except(capabilities.Select(c => c.Code), StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Katalogda olmayan yetenek: {string.Join(", ", unknown)}.", nameof(capabilityCodes));
        }

        var current = await db.ActivityNodeCapabilities.Where(l => l.NodeId == node.Id).ToListAsync(cancellationToken);
        var wantedIds = capabilities.Select(c => c.Id).ToHashSet();
        db.ActivityNodeCapabilities.RemoveRange(current.Where(l => !wantedIds.Contains(l.CapabilityId)));
        db.ActivityNodeCapabilities.AddRange(wantedIds.Except(current.Select(l => l.CapabilityId)).Select(id => new ActivityNodeCapability(node.Id, id)));

        node.Reorder(node.SortOrder); // sürümü artırır: yetenek değişimi de eşzamanlı düzenleme çakışması sayılır
        AuditPlatform(actingUserId, "sector.node.capabilities", new JsonObject
        {
            ["code"] = node.Code,
            ["capabilities"] = new JsonArray([.. wanted.Order(StringComparer.Ordinal).Select(c => JsonValue.Create(c))]),
        });
        await SaveAsync(cancellationToken);
        return ToView(node, [.. wanted.Order(StringComparer.Ordinal)]);
    }

    public async Task<CapabilityView> CreateCapabilityAsync(Guid actingUserId, CapabilityInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var capability = new Capability(input.Code, input.Name);
        if (await db.Capabilities.AnyAsync(c => c.Code == input.Code, cancellationToken))
        {
            throw new SectorCatalogConflictException($"Bu kodla bir yetenek zaten var: '{input.Code}'.");
        }

        db.Capabilities.Add(capability);
        AuditPlatform(actingUserId, "sector.capability.created", new JsonObject { ["code"] = capability.Code, ["name"] = capability.Name });
        await db.SaveChangesAsync(cancellationToken);
        return new CapabilityView(capability.Id, capability.Code, capability.Name, capability.IsActive, []);
    }

    public async Task<CapabilityView> SetCapabilityDependenciesAsync(
        Guid actingUserId, string capabilityCode, IReadOnlyCollection<string> dependsOn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        var capabilities = await db.Capabilities.ToListAsync(cancellationToken);
        var capability = capabilities.SingleOrDefault(c => c.Code == capabilityCode)
            ?? throw new SectorCatalogNotFoundException($"Yetenek bulunamadı: '{capabilityCode}'.");
        var byCode = capabilities.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var wanted = dependsOn.Distinct(StringComparer.Ordinal).ToList();
        var unknown = wanted.Where(c => !byCode.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Katalogda olmayan yetenek: {string.Join(", ", unknown)}.", nameof(dependsOn));
        }

        if (wanted.Contains(capability.Code, StringComparer.Ordinal))
        {
            throw new ArgumentException("Yetenek kendine bağlanamaz.", nameof(dependsOn));
        }

        // Döngü denetimi tüm grafla yapılır: değişen yeteneğin yeni bağımlılıkları, diğerlerinin mevcut bağımlılıkları.
        var codes = capabilities.ToDictionary(c => c.Id, c => c.Code);
        var graph = (await db.CapabilityDependencies.AsNoTracking().ToListAsync(cancellationToken))
            .Where(d => d.CapabilityId != capability.Id)
            .GroupBy(d => codes[d.CapabilityId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(d => codes[d.DependsOnId])]);
        graph[capability.Code] = wanted;
        var errors = CapabilityGraphRules.ValidateDependencies(graph);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(dependsOn));
        }

        var current = await db.CapabilityDependencies.Where(d => d.CapabilityId == capability.Id).ToListAsync(cancellationToken);
        var wantedIds = wanted.Select(c => byCode[c].Id).ToHashSet();
        db.CapabilityDependencies.RemoveRange(current.Where(d => !wantedIds.Contains(d.DependsOnId)));
        db.CapabilityDependencies.AddRange(wantedIds.Except(current.Select(d => d.DependsOnId)).Select(id => new CapabilityDependency(capability.Id, id)));

        AuditPlatform(actingUserId, "sector.capability.dependencies", new JsonObject
        {
            ["code"] = capability.Code,
            ["dependsOn"] = new JsonArray([.. wanted.Order(StringComparer.Ordinal).Select(c => JsonValue.Create(c))]),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new CapabilityView(capability.Id, capability.Code, capability.Name, capability.IsActive, [.. wanted.Order(StringComparer.Ordinal)]);
    }

    private async Task<ActivityNode> FindNodeAsync(Guid nodeId, CancellationToken cancellationToken) =>
        await db.ActivityNodes.SingleOrDefaultAsync(n => n.Id == nodeId, cancellationToken)
            ?? throw new SectorCatalogNotFoundException("Faaliyet bulunamadı.");

    private async Task<List<ActivityNodeInfo>> LoadInfosAsync(CancellationToken cancellationToken) =>
        await db.ActivityNodes.AsNoTracking()
            .Select(n => new ActivityNodeInfo(n.Id, n.Code, n.ParentId, n.Kind, n.Name, n.IsActive, n.Readiness))
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<string>> CapabilityCodesAsync(Guid nodeId, CancellationToken cancellationToken) =>
        await db.ActivityNodeCapabilities.AsNoTracking()
            .Where(l => l.NodeId == nodeId)
            .Join(db.Capabilities, l => l.CapabilityId, c => c.Id, (_, c) => c.Code)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SectorCatalogConflictException("Faaliyet siz düzenlerken başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private void AuditPlatform(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(ActivityNode node) => new()
    {
        ["name"] = node.Name,
        ["description"] = node.Description,
        ["sortOrder"] = node.SortOrder,
        ["readiness"] = node.Readiness.ToString(),
    };

    private static ActivityNodeView ToView(ActivityNode n, IReadOnlyList<string> capabilities) =>
        new(n.Id, n.Code, n.ParentId, n.Kind, n.Name, n.Description, n.SortOrder, n.IsActive, n.Readiness, n.Version, capabilities);
}
