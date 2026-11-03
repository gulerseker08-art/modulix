namespace Modulix.Core.Sectors;

/// <summary>Düğüm türü; seviye sayısı sınırlı değildir, yalnızca kökün türü sabittir (<see cref="Sector"/>).</summary>
public enum ActivityNodeKind
{
    Sector,
    Group,
    Activity,
}

/// <summary>Faaliyetin katalogda bulunması ilgili operasyon modülünün hazır olduğunu söylemez; hazırlık durumu ayrıca tutulur.</summary>
public enum ModuleReadiness
{
    CatalogOnly,
    Available,
}

/// <summary>Sektör/faaliyet katalogunun bir düğümü. Referans <see cref="Id"/> ve <see cref="Code"/> iledir; ad değişebilir.</summary>
public sealed record ActivityNodeInfo(
    Guid Id,
    string Code,
    Guid? ParentId,
    ActivityNodeKind Kind,
    string Name,
    bool IsActive,
    ModuleReadiness Readiness = ModuleReadiness.CatalogOnly);

/// <summary>Düğüm taşıma etkisi: <see cref="UsedNodeCount"/> alt ağaçta tenant seçiminde kullanılan düğüm sayısıdır; kullanılan düğüm taşınmaz.</summary>
public sealed record MoveImpact(int DescendantCount, int UsedNodeCount)
{
    public bool Allowed => UsedNodeCount == 0;
}

/// <summary>Faaliyet ağacının yapı kuralları: kod biçimi, üst düğüm, döngü, pasife alma, taşıma etkisi. Saf kurallardır, veri erişimi yoktur.</summary>
public static class ActivityTreeRules
{
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 100;

    /// <summary>Küçük harf, rakam ve tire; sabit koddur, oluşturulduktan sonra değişmez.</summary>
    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= CodeMaxLength && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Kökten başlayıp doğrudan üst düğüme kadar atalar; kendisi dahil değildir. Bozuk (döngülü) veride sonsuz döngüye girmez.</summary>
    public static IReadOnlyList<Guid> Ancestors(IReadOnlyCollection<ActivityNodeInfo> nodes, Guid id)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var byId = nodes.ToDictionary(n => n.Id);
        var chain = new List<Guid>();
        var seen = new HashSet<Guid> { id };
        Guid? current = byId.GetValueOrDefault(id)?.ParentId;
        while (current is { } parent && byId.ContainsKey(parent) && seen.Add(parent))
        {
            chain.Add(parent);
            current = byId[parent].ParentId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>Düğümün bütün alt düğümleri (doğrudan ve dolaylı); kendisi dahil değildir.</summary>
    public static IReadOnlyList<Guid> Descendants(IReadOnlyCollection<ActivityNodeInfo> nodes, Guid id)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var children = nodes.Where(n => n.ParentId.HasValue).ToLookup(n => n.ParentId!.Value, n => n.Id);
        var result = new List<Guid>();
        var seen = new HashSet<Guid> { id };
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        while (queue.TryDequeue(out Guid current))
        {
            foreach (Guid child in children[current])
            {
                if (seen.Add(child))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Yeni ya da taşınan düğümün üst düğümünü doğrular. <paramref name="nodeId"/> yeni düğümde null'dır.
    /// Yalnızca <see cref="ActivityNodeKind.Sector"/> kök olabilir; diğerleri bir üst düğüm ister.
    /// </summary>
    public static IReadOnlyList<string> ValidateParent(IReadOnlyCollection<ActivityNodeInfo> nodes, Guid? nodeId, ActivityNodeKind kind, Guid? parentId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var errors = new List<string>();
        if (kind == ActivityNodeKind.Sector)
        {
            if (parentId is not null)
            {
                errors.Add("Sektör üst düğüm alamaz.");
            }

            return errors;
        }

        if (parentId is not { } parent)
        {
            errors.Add("Faaliyet grubu ve alt faaliyet için üst düğüm gerekir.");
            return errors;
        }

        if (parent == nodeId)
        {
            errors.Add("Düğüm kendisine bağlanamaz.");
            return errors;
        }

        ActivityNodeInfo? target = nodes.FirstOrDefault(n => n.Id == parent);
        if (target is null)
        {
            errors.Add("Üst düğüm bulunamadı.");
            return errors;
        }

        if (!target.IsActive)
        {
            errors.Add("Pasif düğümün altına düğüm eklenemez.");
        }

        if (nodeId is { } self && Ancestors(nodes, parent).Contains(self))
        {
            errors.Add("Düğüm kendi alt düğümüne bağlanamaz (döngü).");
        }

        return errors;
    }

    /// <summary>Aktif alt düğümü olan düğüm pasife alınamaz: alt düğümler sessizce kapanmaz.</summary>
    public static IReadOnlyList<string> ValidateDeactivate(IReadOnlyCollection<ActivityNodeInfo> nodes, Guid id)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var active = Descendants(nodes, id).Where(d => nodes.First(n => n.Id == d).IsActive).Count();
        return active == 0 ? [] : [$"Aktif {active} alt düğüm var; önce onları pasife alın."];
    }

    /// <summary>Taşıma etkisi; <paramref name="usedNodeIds"/> tenant seçimlerinde kullanılan düğümlerdir.</summary>
    public static MoveImpact Impact(IReadOnlyCollection<ActivityNodeInfo> nodes, Guid id, IReadOnlySet<Guid> usedNodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(usedNodeIds);
        IReadOnlyList<Guid> descendants = Descendants(nodes, id);
        var used = descendants.Append(id).Count(usedNodeIds.Contains);
        return new MoveImpact(descendants.Count, used);
    }
}
