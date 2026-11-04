using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Sectors;

/// <summary>
/// Tenant'ın faaliyet seçimi (ADR-0029 K3/K4). Seçim <c>core.ActivitySelections</c>'dadır (RLS + EF süzgeci); katalog platformdan
/// salt okunur okunur. Seçimin tamamı tek işlemde değişir ve <c>core.activity-selection</c> olarak denetime yazılır.
/// </summary>
internal sealed class TenantActivitySelectionService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit) : ITenantActivitySelection
{
    public async Task<TenantActivitySelectionView> GetAsync(CancellationToken cancellationToken)
    {
        _ = RequireTenant();
        var selected = await db.ActivitySelections.AsNoTracking().Select(s => s.NodeId).ToListAsync(cancellationToken);
        return new TenantActivitySelectionView([.. selected.Order()], await PreviewCoreAsync(selected.ToHashSet(), cancellationToken));
    }

    public async Task<SelectionPreview> PreviewAsync(IReadOnlyCollection<Guid> nodeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        _ = RequireTenant();
        return await PreviewCoreAsync(nodeIds.ToHashSet(), cancellationToken);
    }

    public async Task<TenantActivitySelectionView> SaveAsync(IReadOnlyCollection<Guid> nodeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var tenant = RequireTenant();
        var wanted = nodeIds.ToHashSet();
        var preview = await PreviewCoreAsync(wanted, cancellationToken);
        if (preview.Errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", preview.Errors), nameof(nodeIds));
        }

        var current = await db.ActivitySelections.ToListAsync(cancellationToken);
        var currentIds = current.Select(s => s.NodeId).ToHashSet();
        if (!currentIds.SetEquals(wanted))
        {
            var codes = await db.ActivityNodes.AsNoTracking().ToDictionaryAsync(n => n.Id, n => n.Code, cancellationToken);
            var before = Snapshot(currentIds, codes);
            db.ActivitySelections.RemoveRange(current.Where(s => !wanted.Contains(s.NodeId)));
            db.ActivitySelections.AddRange(wanted.Except(currentIds).Select(id => new TenantActivitySelection(id)));
            audit.Add(new AuditEntry(
                tenant.TenantId,
                tenant.UserId,
                CoreModule.AuditedEntityKeys.ActivitySelection,
                tenant.TenantId,
                AuditAction.Updated,
                RecordDiff.Compute(before, Snapshot(wanted, codes)),
                ExecutionSource.Api,
                Activity.Current?.TraceId.ToString()));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new TenantActivitySelectionView([.. wanted.Order()], preview);
    }

    private async Task<SelectionPreview> PreviewCoreAsync(IReadOnlySet<Guid> selected, CancellationToken cancellationToken)
    {
        var nodes = await db.ActivityNodes.AsNoTracking()
            .Select(n => new ActivityNodeInfo(n.Id, n.Code, n.ParentId, n.Kind, n.Name, n.IsActive, n.Readiness))
            .ToListAsync(cancellationToken);
        var capabilities = await db.Capabilities.AsNoTracking().ToListAsync(cancellationToken);
        var codes = capabilities.ToDictionary(c => c.Id, c => c.Code);
        var nodeCapabilities = (await db.ActivityNodeCapabilities.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(l => l.NodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(l => codes[l.CapabilityId])]);
        var dependencies = (await db.CapabilityDependencies.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(d => codes[d.CapabilityId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(d => codes[d.DependsOnId])]);

        // Sağlanan yetenek = katalogda etkin olanlar. Modüllerin yetenek beyanı sonraki dilimdedir; o zamana kadar eksik yetenek
        // yalnızca pasife alınmış yetenekler için çıkar.
        var available = capabilities.Where(c => c.IsActive).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        return ActivitySelectionRules.Preview(nodes, selected, nodeCapabilities, dependencies, available);
    }

    private static JsonObject Snapshot(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, string> codes) => new()
    {
        ["activities"] = new JsonArray([.. ids.Select(id => codes.GetValueOrDefault(id, id.ToString())).Order(StringComparer.Ordinal).Select(c => JsonValue.Create(c))]),
    };

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan faaliyet seçimi yapılamaz.");
}
