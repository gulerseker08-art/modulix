namespace Modulix.Core.Sectors;

/// <summary>Başlangıç paketinin yeteneği; bağımlılıklar yalnızca pakette tanımlı yeteneklere gidebilir.</summary>
public sealed record StarterPackageCapability(string Code, string Name, IReadOnlyList<string> DependsOn);

/// <summary>Başlangıç paketinin düğümü. <see cref="ParentCode"/> boşsa sektör (kök) olmalıdır; üst düğüm pakette tanımlı olmalıdır.</summary>
public sealed record StarterPackageNode(
    string Code,
    string? ParentCode,
    ActivityNodeKind Kind,
    string Name,
    string? Description,
    int SortOrder,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Sektör kataloğu başlangıç paketi (ADR-0029 Dilim D): ağaç, yetenek ve düğüm–yetenek eşlemesi için sürümlü, ürün tarafından önerilen
/// bir başlangıç. Veridir, kod değildir: Core sektör bilmez, içerik gömülü tanım dosyasından gelir. <see cref="Verified"/> false ise sınıflandırma
/// bir öneridir ve gerçek bir işletmeyle doğrulanmamıştır; bunu kurulumda gösteririz.
/// </summary>
public sealed record StarterPackage(
    string Code,
    string Name,
    string? Description,
    bool Verified,
    string? VerificationNote,
    IReadOnlyList<StarterPackageCapability> Capabilities,
    IReadOnlyList<StarterPackageNode> Nodes);

// Completions: pakette tanımlı olduğu halde boş kalmış bağımlılık ("dependencies:kod") ve düğüm–yetenek eşlemesi ("capabilities:kod"); yarım kalmış kurulumu tamamlar,
// dolu olanlara dokunmaz.
/// <summary>Kurulumun ne yapacağı (ya da yaptığı). Mevcut kayıt koda göre bulunur ve <b>hiçbir zaman değiştirilmez</b>; farklıysa uyarı olur.</summary>
public sealed record StarterPackagePlan(
    IReadOnlyList<string> CapabilitiesToCreate,
    IReadOnlyList<string> CapabilitiesExisting,
    IReadOnlyList<string> NodesToCreate,
    IReadOnlyList<string> NodesExisting,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Completions)
{
    public bool NothingToDo => CapabilitiesToCreate.Count == 0 && NodesToCreate.Count == 0 && Completions.Count == 0;
}

public sealed record StarterPackageSummary(string Code, string Name, string? Description, bool Verified, string? VerificationNote, int NodeCount, int CapabilityCount);

/// <summary>Bilinmeyen başlangıç paketi.</summary>
public sealed class StarterPackageNotFoundException(string message) : Exception(message);

/// <summary>
/// Platform yöneticisinin başlangıç paketi kurulumu. Kurulum yalnızca ekler (mevcut kodları değiştirmez, silmez, tenant seçimlerine dokunmaz) ve
/// tekrar çalıştırılabilir; her yazma <see cref="ISectorCatalog"/> üzerinden yapıldığından aynı kurallara ve platform denetim kaydına tabidir.
/// Kurulum tek işlem değildir: yarıda kalırsa yeniden çalıştırmak kalanı tamamlar.
/// </summary>
public interface IStarterPackages
{
    IReadOnlyList<StarterPackageSummary> List();

    /// <summary>Yazmadan, kurulacakları ve atlanacakları gösterir.</summary>
    Task<StarterPackagePlan> PreviewAsync(string packageCode, CancellationToken cancellationToken);

    Task<StarterPackagePlan> InstallAsync(Guid actingUserId, string packageCode, CancellationToken cancellationToken);
}

/// <summary>Başlangıç paketi tanım denetimi: yapı bozuksa kurulumdan önce reddedilir. Saf kurallardır.</summary>
public static class StarterPackageRules
{
    public static IReadOnlyList<string> Validate(StarterPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();

        if (!ActivityTreeRules.IsValidCode(package.Code))
        {
            errors.Add($"Paket kodu geçersiz: '{package.Code}'.");
        }

        var capabilityCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in package.Capabilities)
        {
            if (!capabilityCodes.Add(capability.Code))
            {
                errors.Add($"Yetenek kodu tekrar ediyor: '{capability.Code}'.");
            }
        }

        var graph = package.Capabilities
            .GroupBy(c => c.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DependsOn);
        foreach (var capability in package.Capabilities)
        {
            errors.AddRange(capability.DependsOn.Where(d => !capabilityCodes.Contains(d)).Select(d => $"'{capability.Code}' pakette olmayan yeteneğe bağlı: '{d}'."));
        }

        errors.AddRange(CapabilityGraphRules.ValidateDependencies(graph.ToDictionary(p => p.Key, p => p.Value)));

        var nodeCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in package.Nodes)
        {
            if (!ActivityTreeRules.IsValidCode(node.Code))
            {
                errors.Add($"Düğüm kodu geçersiz: '{node.Code}'.");
            }

            if (!nodeCodes.Add(node.Code))
            {
                errors.Add($"Düğüm kodu tekrar ediyor: '{node.Code}'.");
            }

            if (string.IsNullOrWhiteSpace(node.Name) || node.Name.Trim().Length > ActivityTreeRules.NameMaxLength)
            {
                errors.Add($"Düğüm adı boş olamaz ve en çok {ActivityTreeRules.NameMaxLength} karakter olabilir: '{node.Code}'.");
            }

            if ((node.Kind == ActivityNodeKind.Sector) != (node.ParentCode is null))
            {
                errors.Add($"Yalnızca sektör kök olabilir; diğer düğümler üst düğüm ister: '{node.Code}'.");
            }

            errors.AddRange(node.Capabilities.Where(c => !capabilityCodes.Contains(c)).Select(c => $"'{node.Code}' pakette olmayan yeteneği istiyor: '{c}'."));
        }

        foreach (var node in package.Nodes.Where(n => n.ParentCode is not null && !nodeCodes.Contains(n.ParentCode)))
        {
            errors.Add($"Üst düğüm pakette yok: '{node.Code}' → '{node.ParentCode}'.");
        }

        // Döngü: üst zinciri izleyen yürüyüş en çok düğüm sayısı kadar adım sürer.
        var parents = package.Nodes.GroupBy(n => n.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().ParentCode, StringComparer.Ordinal);
        foreach (var code in parents.Keys)
        {
            var current = parents[code];
            var steps = 0;
            while (current is not null && parents.TryGetValue(current, out var next) && steps++ <= parents.Count)
            {
                current = next;
            }

            if (steps > parents.Count)
            {
                errors.Add($"Üst düğüm zinciri döngü içeriyor: '{code}'.");
            }
        }

        return errors;
    }

    /// <summary>Düğümleri üst düğümler önce gelecek sırayla verir. Önce <see cref="Validate"/> geçmiş olmalıdır.</summary>
    public static IReadOnlyList<StarterPackageNode> InParentOrder(StarterPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var ordered = new List<StarterPackageNode>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = package.Nodes.OrderBy(n => n.SortOrder).ThenBy(n => n.Code, StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(n => n.ParentCode is null || placed.Contains(n.ParentCode)).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException("Paket doğrulanmadan sıralandı: döngü ya da eksik üst düğüm.");
            }

            foreach (var node in ready)
            {
                ordered.Add(node);
                placed.Add(node.Code);
                remaining.Remove(node);
            }
        }

        return ordered;
    }
}
