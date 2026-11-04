namespace Modulix.Core.Sectors;

/// <summary>Karşılanmayan yetenek ve onu gerektiren seçili düğümler.</summary>
public sealed record CapabilityGap(string Capability, IReadOnlyList<Guid> RequiredByNodes);

/// <summary>
/// Tenant seçiminin önizlemesi. <see cref="ContextNodeIds"/> seçili düğümlerin üst bağlamıdır: türetilir, seçilmiş sayılmaz ve
/// alt faaliyetleri etkinleştirmez. <see cref="CatalogOnlyNodeIds"/> seçili ama operasyon modülü hazır olmayan düğümlerdir.
/// </summary>
public sealed record SelectionPreview(
    IReadOnlyList<Guid> ContextNodeIds,
    IReadOnlyList<string> Errors,
    IReadOnlyList<CapabilityGap> Gaps,
    IReadOnlyList<Guid> CatalogOnlyNodeIds)
{
    public bool IsComplete => Errors.Count == 0 && Gaps.Count == 0;
}

/// <summary>Seçimin kuralları: üst bağlam türetme, pasif/bilinmeyen düğüm, eksik yetenek bağımlılıkları.</summary>
public static class ActivitySelectionRules
{
    /// <param name="nodes">Katalog düğümleri.</param>
    /// <param name="selected">Tenant'ın açıkça seçtiği düğümler.</param>
    /// <param name="nodeCapabilities">Düğüm → gerektirdiği yetenek kodları.</param>
    /// <param name="capabilityDependencies">Yetenek → doğrudan bağımlı olduğu yetenekler (sınıflandırma ağacından ayrı graf).</param>
    /// <param name="availableCapabilities">Uygulamanın sağladığı yetenekler (açık modüllerin beyanı).</param>
    public static SelectionPreview Preview(
        IReadOnlyCollection<ActivityNodeInfo> nodes,
        IReadOnlySet<Guid> selected,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> nodeCapabilities,
        IReadOnlyDictionary<string, IReadOnlyList<string>> capabilityDependencies,
        IReadOnlySet<string> availableCapabilities)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(nodeCapabilities);
        ArgumentNullException.ThrowIfNull(capabilityDependencies);
        ArgumentNullException.ThrowIfNull(availableCapabilities);

        var byId = nodes.ToDictionary(n => n.Id);
        var errors = new List<string>();
        var valid = new List<ActivityNodeInfo>();
        foreach (Guid id in selected.Order())
        {
            if (!byId.TryGetValue(id, out ActivityNodeInfo? node))
            {
                errors.Add($"Seçilen düğüm katalogda yok: {id}.");
            }
            else if (!node.IsActive)
            {
                errors.Add($"Pasif faaliyet seçilemez: {node.Name}.");
            }
            else
            {
                valid.Add(node);
            }
        }

        var context = valid
            .SelectMany(n => ActivityTreeRules.Ancestors(nodes, n.Id))
            .Where(a => !selected.Contains(a))
            .Distinct()
            .ToList();

        var catalogOnly = valid.Where(n => n.Readiness == ModuleReadiness.CatalogOnly).Select(n => n.Id).ToList();

        var requiredBy = new SortedDictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (ActivityNodeInfo node in valid)
        {
            foreach (string capability in nodeCapabilities.GetValueOrDefault(node.Id) ?? [])
            {
                foreach (string needed in CapabilityGraphRules.Closure(capability, capabilityDependencies))
                {
                    if (availableCapabilities.Contains(needed))
                    {
                        continue;
                    }

                    if (!requiredBy.TryGetValue(needed, out List<Guid>? list))
                    {
                        list = [];
                        requiredBy[needed] = list;
                    }

                    if (!list.Contains(node.Id))
                    {
                        list.Add(node.Id);
                    }
                }
            }
        }

        var gaps = requiredBy.Select(p => new CapabilityGap(p.Key, p.Value)).ToList();
        return new SelectionPreview(context, errors, gaps, catalogOnly);
    }
}

/// <summary>Yetenek bağımlılık grafiği; sınıflandırma ağacından ayrıdır.</summary>
public static class CapabilityGraphRules
{
    /// <summary>Yetenek ve doğrudan/dolaylı bağımlı olduğu bütün yetenekler (kendisi dahil); döngüde sonsuz döngüye girmez.</summary>
    public static IReadOnlyList<string> Closure(string capability, IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var seen = new HashSet<string>(StringComparer.Ordinal) { capability };
        var result = new List<string> { capability };
        var stack = new Stack<string>();
        stack.Push(capability);
        while (stack.TryPop(out string? current))
        {
            foreach (string next in dependencies.GetValueOrDefault(current) ?? [])
            {
                if (seen.Add(next))
                {
                    result.Add(next);
                    stack.Push(next);
                }
            }
        }

        return result;
    }

    /// <summary>Bağımlılık grafiğinde döngü ya da kendine bağlanma varsa hata döner; yayın öncesi denetimdir.</summary>
    public static IReadOnlyList<string> ValidateDependencies(IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var errors = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string node)
        {
            if (done.Contains(node))
            {
                return;
            }

            int index = path.IndexOf(node);
            if (index >= 0)
            {
                errors.Add($"Yetenek bağımlılığında döngü: {string.Join(" → ", path.Skip(index).Append(node))}.");
                return;
            }

            path.Add(node);
            foreach (string next in dependencies.GetValueOrDefault(node) ?? [])
            {
                Visit(next);
            }

            path.RemoveAt(path.Count - 1);
            done.Add(node);
        }

        foreach (string node in dependencies.Keys.Order(StringComparer.Ordinal))
        {
            Visit(node);
        }

        return errors;
    }
}
