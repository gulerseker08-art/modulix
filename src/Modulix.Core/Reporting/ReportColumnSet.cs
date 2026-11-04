using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Customization;

namespace Modulix.Core.Reporting;

/// <summary>
/// Bir veri kümesinin çağıran için kullanılabilir sütunları: modülün bildirdikleri, firmanın özel alanları ve çağıranın göremediği
/// (hassas) alanlar. Özel alan sütunu "custom.anahtar" adını taşır; anahtar değişmediği için alan yeniden adlandırılınca rapor bozulmaz,
/// alan arşivlenince sütun <see cref="Removed"/> olur ve rapor ne olduğunu söyleyerek çalışmaz.
/// </summary>
public sealed class ReportColumnSet
{
    private readonly Dictionary<string, DatasetColumn> available;
    private readonly HashSet<string> hidden;
    private readonly HashSet<string> removed;

    private ReportColumnSet(DatasetDefinition dataset, Dictionary<string, DatasetColumn> available, HashSet<string> hidden, HashSet<string> removed)
    {
        Dataset = dataset;
        this.available = available;
        this.hidden = hidden;
        this.removed = removed;
    }

    public DatasetDefinition Dataset { get; }

    /// <summary>Sütun, sütun adıyla; çağıranın göremediği ve kaldırılmış sütunlar yoktur.</summary>
    public DatasetColumn? Find(string field) => available.GetValueOrDefault(field);

    public bool IsHidden(string field) => hidden.Contains(field);

    public bool IsRemoved(string field) => removed.Contains(field);

    public IEnumerable<DatasetColumn> Columns => available.Values;

    /// <param name="hiddenFields">Çağıranın izni olmayan hassas alanlar (kayıt türü için).</param>
    /// <param name="customFields">Kayıt türünün özel alanları (arşivliler dahil); veri kümesi özel alan kabul etmiyorsa yok sayılır.</param>
    public static ReportColumnSet Build(
        DatasetDefinition dataset, IReadOnlySet<string> hiddenFields, IEnumerable<CustomFieldInfo> customFields)
    {
        var available = new Dictionary<string, DatasetColumn>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var column in dataset.Columns)
        {
            if (hiddenFields.Contains(column.Field) || (column.CurrencyField is { } currency && hiddenFields.Contains(currency)))
            {
                hidden.Add(column.Field);
                continue;
            }

            available[column.Field] = column;
        }

        if (dataset.AllowsCustomFields)
        {
            foreach (var field in customFields.Where(f => f.EntityKey == dataset.EntityKey))
            {
                var name = RecordFieldNames.Custom(field.Key);
                if (field.Archived)
                {
                    removed.Add(name);
                    continue;
                }

                available[name] = FromCustomField(field, name);
            }
        }

        return new ReportColumnSet(dataset, available, hidden, removed);
    }

    /// <summary>Özel alan tipine göre sütun: yalnızca anlamlı kullanımlar açılır (çoklu seçim yalnızca gösterilir, metin gruplanmaz).</summary>
    private static DatasetColumn FromCustomField(CustomFieldInfo field, string name) => field.Type switch
    {
        CustomFieldType.Number => new DatasetColumn(name, field.Label, DatasetColumnKind.Number, ColumnUse.Measure | ColumnUse.Filter | ColumnUse.Show),
        CustomFieldType.Date => new DatasetColumn(name, field.Label, DatasetColumnKind.Date, ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show),
        CustomFieldType.YesNo => new DatasetColumn(name, field.Label, DatasetColumnKind.Flag, ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show),
        CustomFieldType.Choice => new DatasetColumn(name, field.Label, DatasetColumnKind.Choice, ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show)
        {
            Options = [.. field.Options.Select(o => new DatasetOption(o.Key, o.Label))],
        },
        CustomFieldType.MultiChoice => new DatasetColumn(name, field.Label, DatasetColumnKind.Text, ColumnUse.Show),
        _ => new DatasetColumn(name, field.Label, DatasetColumnKind.Text, ColumnUse.Filter | ColumnUse.Show),
    };
}

public static class RecordFieldNames
{
    public const string CustomPrefix = RecordFields.CustomPrefix;

    public static string Custom(string key) => CustomPrefix + key;

    public static bool IsCustom(string field) => field.StartsWith(CustomPrefix, StringComparison.Ordinal);

    public static string CustomKey(string field) => field[CustomPrefix.Length..];
}
