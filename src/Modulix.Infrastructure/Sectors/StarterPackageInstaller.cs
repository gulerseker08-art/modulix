using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modulix.Core.Sectors;

namespace Modulix.Infrastructure.Sectors;

/// <summary>
/// Başlangıç paketlerini gömülü tanım dosyalarından okur ve kataloğa yalnızca ekler (ADR-0029 Dilim D). Mevcut kod bulunursa dokunulmaz;
/// ad, üst düğüm ya da tür farklıysa uyarı olur. Yetenek bağımlılığı ve düğüm–yetenek eşlemesi yalnızca bu kurulumda oluşan kayıtlara yazılır,
/// böylece platform yöneticisinin sonradan yaptığı düzenleme ezilmez. Yazma <see cref="ISectorCatalog"/> üzerinden yapılır.
/// </summary>
internal sealed class StarterPackageInstaller(ISectorCatalog catalog) : IStarterPackages
{
    private const string ResourcePrefix = "Modulix.StarterPackages.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<IReadOnlyDictionary<string, StarterPackage>> Packages = new(Load);

    public IReadOnlyList<StarterPackageSummary> List() =>
    [
        .. Packages.Value.Values.OrderBy(p => p.Code, StringComparer.Ordinal)
            .Select(p => new StarterPackageSummary(p.Code, p.Name, p.Description, p.Verified, p.VerificationNote, p.Nodes.Count, p.Capabilities.Count)),
    ];

    public async Task<StarterPackagePlan> PreviewAsync(string packageCode, CancellationToken cancellationToken) =>
        (await PlanAsync(packageCode, cancellationToken)).Plan;

    public async Task<StarterPackagePlan> InstallAsync(Guid actingUserId, string packageCode, CancellationToken cancellationToken)
    {
        var (package, plan, current) = await PlanAsync(packageCode, cancellationToken);
        var createdCapabilities = new HashSet<string>(plan.CapabilitiesToCreate, StringComparer.Ordinal);
        var createdNodes = new HashSet<string>(plan.NodesToCreate, StringComparer.Ordinal);

        foreach (var capability in package.Capabilities.Where(c => createdCapabilities.Contains(c.Code)))
        {
            await catalog.CreateCapabilityAsync(actingUserId, new CapabilityInput(capability.Code, capability.Name), cancellationToken);
        }

        foreach (var capability in package.Capabilities.Where(c => createdCapabilities.Contains(c.Code) && c.DependsOn.Count > 0))
        {
            await catalog.SetCapabilityDependenciesAsync(actingUserId, capability.Code, capability.DependsOn, cancellationToken);
        }

        var ids = current.Nodes.ToDictionary(n => n.Code, n => n.Id, StringComparer.Ordinal);
        foreach (var node in StarterPackageRules.InParentOrder(package).Where(n => createdNodes.Contains(n.Code)))
        {
            var parentId = node.ParentCode is null ? (Guid?)null : ids[node.ParentCode];
            var created = await catalog.CreateNodeAsync(
                actingUserId, new ActivityNodeInput(node.Code, parentId, node.Kind, node.Name, node.Description, node.SortOrder), cancellationToken);
            ids[node.Code] = created.Id;
            if (node.Capabilities.Count > 0)
            {
                await catalog.SetNodeCapabilitiesAsync(actingUserId, created.Id, node.Capabilities, cancellationToken);
            }
        }

        return plan;
    }

    private async Task<(StarterPackage Package, StarterPackagePlan Plan, SectorCatalogView Current)> PlanAsync(string packageCode, CancellationToken cancellationToken)
    {
        if (!Packages.Value.TryGetValue(packageCode ?? string.Empty, out var package))
        {
            throw new StarterPackageNotFoundException($"Başlangıç paketi bulunamadı: '{packageCode}'.");
        }

        var errors = StarterPackageRules.Validate(package);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Paket tanımı geçersiz: " + string.Join(" ", errors), nameof(packageCode));
        }

        var current = await catalog.GetAsync(cancellationToken);
        var existingCapabilities = current.Capabilities.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var nodesByCode = current.Nodes.ToDictionary(n => n.Code, StringComparer.Ordinal);
        var codeOfId = current.Nodes.ToDictionary(n => n.Id, n => n.Code);

        var warnings = new List<string>();
        var nodesToCreate = new List<string>();
        var nodesExisting = new List<string>();
        foreach (var node in StarterPackageRules.InParentOrder(package))
        {
            if (!nodesByCode.TryGetValue(node.Code, out var existing))
            {
                nodesToCreate.Add(node.Code);
                continue;
            }

            nodesExisting.Add(node.Code);
            var existingParent = existing.ParentId is { } parentId ? codeOfId.GetValueOrDefault(parentId) : null;
            if (existing.Kind != node.Kind || !string.Equals(existingParent, node.ParentCode, StringComparison.Ordinal))
            {
                warnings.Add($"'{node.Code}' katalogda farklı bir konumda ya da türde var; değiştirilmedi.");
            }
            else if (!existing.IsActive)
            {
                warnings.Add($"'{node.Code}' katalogda pasif; açılmadı.");
            }
        }

        // Atlanan bir üst düğümün altına yeni düğüm açılacaksa üst düğüm etkin olmalıdır; yoksa kurulum yarıda kalırdı.
        foreach (var node in package.Nodes.Where(n => nodesToCreate.Contains(n.Code) && n.ParentCode is not null))
        {
            if (nodesByCode.TryGetValue(node.ParentCode!, out var parent) && !parent.IsActive)
            {
                throw new ArgumentException($"'{node.ParentCode}' pasif; altına '{node.Code}' eklenemez. Önce üst düğümü açın.", nameof(packageCode));
            }
        }

        var plan = new StarterPackagePlan(
            [.. package.Capabilities.Where(c => !existingCapabilities.Contains(c.Code)).Select(c => c.Code)],
            [.. package.Capabilities.Where(c => existingCapabilities.Contains(c.Code)).Select(c => c.Code)],
            nodesToCreate,
            nodesExisting,
            warnings);
        return (package, plan, current);
    }

    private static IReadOnlyDictionary<string, StarterPackage> Load()
    {
        var assembly = typeof(StarterPackageInstaller).Assembly;
        var packages = new Dictionary<string, StarterPackage>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var package = JsonSerializer.Deserialize<StarterPackage>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Başlangıç paketi okunamadı: {name}.");
            packages[package.Code] = package;
        }

        return packages;
    }
}
