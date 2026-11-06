using Modulix.Core.Modules;
using Modulix.Core.Records;

namespace Modulix.Infrastructure.Authorization;

/// <summary>Kapsamı kayıt türüne göre ayarlanabilen bir kayıt türü (ADR-0030).</summary>
internal sealed record ScopedRecordType(string EntityKey, string ModuleKey, string Label);

/// <summary>
/// Veri kapsamı uygulanan üst düzey kayıt türleri: sorumlu alanı olan ve kapsamı kendi anahtarıyla çözülen kayıt depoları
/// (ADR-0030). Alt kayıt türleri (ör. fırsat iletişimcisi) ana türünün kapsamını izlediği için listede yer almaz. Liste koda
/// sabit yazılmaz; modüllerin kaydettiği depolardan gelir, görünen ad denetim ekranı tanımlarından alınır.
/// </summary>
internal sealed class ScopedRecordTypes(IRecordStoreRegistry stores, ModuleCatalog catalog)
{
    /// <summary>Açık modüllerin kapsam türleri, anahtar sırasıyla.</summary>
    public IReadOnlyList<ScopedRecordType> ForModules(IReadOnlySet<string> enabledModules) =>
        [.. All().Where(t => catalog.IsEnabled(t.ModuleKey, enabledModules))];

    /// <summary>Modülü açık olsun olmasın bütün kapsam türleri; modülü sonradan kapanan türün istisnası geçerli kalır.</summary>
    public IReadOnlyList<ScopedRecordType> All()
    {
        var labels = catalog.AuditedEntities.ToDictionary(e => e.EntityKey, e => e.DisplayName, StringComparer.OrdinalIgnoreCase);
        return [.. stores.Stores
            .Where(s => s.UsesOwnerScope && string.Equals(s.ScopeEntityKey, s.EntityKey, StringComparison.OrdinalIgnoreCase))
            .Select(s => new ScopedRecordType(s.EntityKey, AccessKeys.ModuleOf(s.EntityKey), labels.GetValueOrDefault(s.EntityKey, s.EntityKey)))
            .OrderBy(t => t.EntityKey, StringComparer.Ordinal)];
    }
}
