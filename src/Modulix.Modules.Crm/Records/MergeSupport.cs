using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Firma ve kişi birleştirmesinin ortak yardımcıları (ADR-0013).</summary>
internal static class MergeSupport
{
    private const string CustomPrefix = RecordFields.CustomPrefix;

    public static async Task<MergeLink?> FindLinkAsync(IModuleData data, string entityKey, Guid sourceId, CancellationToken cancellationToken)
    {
        var merged = await data.Set<MergedRecord>().AsNoTracking()
            .Where(m => m.EntityKey == entityKey && m.SourceId == sourceId)
            .SingleOrDefaultAsync(cancellationToken);
        return merged is null ? null : new MergeLink(merged.EntityKey, merged.SourceId, merged.TargetId, merged.CreatedAt, merged.MergedBy);
    }

    public static void AddLink(IModuleData data, string entityKey, Guid sourceId, Guid targetId, Guid? mergedBy) =>
        data.Set<MergedRecord>().Add(new MergedRecord { EntityKey = entityKey, SourceId = sourceId, TargetId = targetId, MergedBy = mergedBy });

    /// <summary>Alan seçimlerini doğrular: bilinen alan ve "source"/"target" değeri. Bilinmeyen alan sessizce yok sayılmaz.</summary>
    public static IReadOnlyDictionary<string, string> ValidateChoices(IReadOnlyDictionary<string, string>? choices, IEnumerable<string> fieldNames)
    {
        var known = fieldNames.ToHashSet(StringComparer.Ordinal);
        var errors = new List<RecordFieldError>();
        foreach (var (field, choice) in choices ?? new Dictionary<string, string>())
        {
            if (!known.Contains(field) && !field.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                errors.Add(new RecordFieldError($"fieldChoices.{field}", "Bilinmeyen alan."));
            }
            else if (choice is not (MergeRequest.FromSource or MergeRequest.FromTarget))
            {
                errors.Add(new RecordFieldError($"fieldChoices.{field}", "Değer \"source\" veya \"target\" olmalıdır."));
            }
        }

        return errors.Count > 0 ? throw new RecordValidationException(errors) : choices ?? new Dictionary<string, string>();
    }

    private static bool IsEmpty(JsonNode? node) =>
        node is null || (node is JsonValue v && v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)) || node is JsonArray { Count: 0 };

    /// <summary>İki kaydın farklı alanları (özel alanlar dahil). Hedef boş, kaynak doluysa kaynak önerilir.</summary>
    public static List<MergeFieldDiff> Diff<TEntity>(
        IEnumerable<RecordField<TEntity>> fields, TEntity source, TEntity target, string? sourceCustom, string? targetCustom, params string[] skip)
    {
        var diffs = new List<MergeFieldDiff>();
        foreach (var field in fields.Where(f => !skip.Contains(f.Name)))
        {
            var (s, t) = (field.Read(source), field.Read(target));
            if (!JsonNode.DeepEquals(s, t) && !(IsEmpty(s) && IsEmpty(t)))
            {
                diffs.Add(new MergeFieldDiff(field.Name, s?.DeepClone(), t?.DeepClone(), IsEmpty(t) && !IsEmpty(s) ? MergeRequest.FromSource : MergeRequest.FromTarget));
            }
        }

        var (src, tgt) = (Parse(sourceCustom), Parse(targetCustom));
        foreach (var key in src.Select(p => p.Key).Union(tgt.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var (s, t) = (src[key], tgt[key]);
            if (!JsonNode.DeepEquals(s, t) && !(IsEmpty(s) && IsEmpty(t)))
            {
                diffs.Add(new MergeFieldDiff(CustomPrefix + key, s?.DeepClone(), t?.DeepClone(), IsEmpty(t) && !IsEmpty(s) ? MergeRequest.FromSource : MergeRequest.FromTarget));
            }
        }

        return diffs;
    }

    /// <summary>Özel alanlar: hedefte olmayan anahtar kaynaktan gelir, ikisinde de varsa hedef kalır; açık "source" seçimi kaynağı yazar.</summary>
    public static string? MergeCustom(string? sourceCustom, string? targetCustom, IReadOnlyDictionary<string, string> choices)
    {
        var (src, tgt) = (Parse(sourceCustom), Parse(targetCustom));
        foreach (var (key, value) in src)
        {
            if (IsEmpty(tgt[key]) || choices.GetValueOrDefault(CustomPrefix + key) == MergeRequest.FromSource)
            {
                tgt[key] = value?.DeepClone();
            }
        }

        return tgt.Count > 0 ? tgt.ToJsonString() : null;
    }

    public static void AddImpact(List<MergeImpact> impacts, string label, int count)
    {
        if (count > 0)
        {
            impacts.Add(new MergeImpact(label, count));
        }
    }

    private static JsonObject Parse(string? json) => json is null ? [] : JsonNode.Parse(json) as JsonObject ?? [];
}

/// <summary>
/// İki satırın içeriğini takas eder (ana işareti, sahip ve kimlik yerinde kalır). Birleştirmede kaynağın ana kaydını öne
/// geçirmek için kullanılır: iki satır arasında "ana" işaretini çevirmek, tek ana kayıt indeksi yüzünden güncelleme sırasına
/// bağlı olarak ihlal verebilir; içerik takası hiçbir sırada iki ana satır oluşturmaz.
/// </summary>
internal static class MergeSwap
{
    public static void Contents(Address a, Address b)
    {
        (a.Type, b.Type) = (b.Type, a.Type);
        (a.Country, b.Country) = (b.Country, a.Country);
        (a.City, b.City) = (b.City, a.City);
        (a.District, b.District) = (b.District, a.District);
        (a.PostalCode, b.PostalCode) = (b.PostalCode, a.PostalCode);
        (a.Line1, b.Line1) = (b.Line1, a.Line1);
        (a.Line2, b.Line2) = (b.Line2, a.Line2);
        (a.NormalizedText, b.NormalizedText) = (b.NormalizedText, a.NormalizedText);
        a.Touch();
        b.Touch();
    }

    public static void Contents(ContactPoint a, ContactPoint b)
    {
        (a.Type, b.Type) = (b.Type, a.Type);
        (a.Value, b.Value) = (b.Value, a.Value);
        (a.NormalizedValue, b.NormalizedValue) = (b.NormalizedValue, a.NormalizedValue);
        (a.IsShared, b.IsShared) = (b.IsShared, a.IsShared);
        (a.Note, b.Note) = (b.Note, a.Note);
        a.Touch();
        b.Touch();
    }
}
