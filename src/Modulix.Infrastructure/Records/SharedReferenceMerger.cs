using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using Modulix.Core.Records;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Birleştirmede ortak kayda başka modüllerin başvurularını bulur ve taşır (ADR-0013). Modüllerin bildirdiği
/// <see cref="Modulix.Core.Contracts.SharedRecords.SharedRecordReference"/> alanları, silme korumasıyla aynı yoldan sayılır
/// (<see cref="IRecordStoreRegistry"/> + filtre). Birleştiren modülün kendi başvuruları (<c>ownerPrefix</c>) deposunca
/// taşındığı için atlanır. Başvuran depo <see cref="IReferenceMover"/> uygulamıyorsa birleştirme engellenir: başvuru eski
/// kayda bakar kalmaz.
/// </summary>
internal sealed class SharedReferenceMerger(ModuleCatalog catalog, IRecordStoreRegistry stores) : ISharedReferenceMerger
{
    public async Task<SharedReferencePlan> PlanAsync(string sharedEntityKey, string ownerPrefix, Guid fromId, CancellationToken cancellationToken)
    {
        var impacts = new List<MergeImpact>();
        var blockers = new List<string>();
        foreach (var reference in External(sharedEntityKey, ownerPrefix))
        {
            if (stores.Find(reference.EntityKey) is not { } store)
            {
                continue;
            }

            var filters = new Dictionary<string, string> { [reference.Field] = fromId.ToString() };
            var page = await store.QueryAsync(new RecordQuery(reference.EntityKey, PageSize: 1, Filters: filters), cancellationToken);
            if (page.TotalCount == 0)
            {
                continue;
            }

            if (store is IReferenceMover)
            {
                impacts.Add(new MergeImpact($"{reference.Label} başvurusu yeni kayda taşınır", page.TotalCount));
            }
            else
            {
                blockers.Add($"{page.TotalCount} {reference.Label} bu kayda başvuruyor ve taşınamıyor; birleştirmeden önce başvuruları kaldırın.");
            }
        }

        return new SharedReferencePlan(impacts, blockers);
    }

    public async Task ApplyAsync(string sharedEntityKey, string ownerPrefix, Guid fromId, Guid toId, CancellationToken cancellationToken)
    {
        foreach (var reference in External(sharedEntityKey, ownerPrefix))
        {
            if (stores.Find(reference.EntityKey) is IReferenceMover mover)
            {
                await mover.RepointAsync(reference.Field, fromId, toId, cancellationToken);
            }
        }
    }

    private IEnumerable<Modulix.Core.Contracts.SharedRecords.SharedRecordReference> External(string sharedEntityKey, string ownerPrefix) =>
        catalog.SharedReferencesTo(sharedEntityKey).Where(r => !r.EntityKey.StartsWith(ownerPrefix, StringComparison.Ordinal));
}
