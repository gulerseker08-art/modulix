using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Reporting;

namespace Modulix.Core.Modules;

/// <summary>Rapor veri kümeleri ve bağlantıları (ADR-0026, dilim 3): modül başına sabit; açılışta doğrulanır, geçersizse uygulama açılmaz.</summary>
public sealed partial class ModuleCatalog
{
    private readonly Dictionary<string, (string ModuleKey, DatasetDefinition Dataset)> datasets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, ReportLink Link)> reportLinks = new(StringComparer.Ordinal);

    /// <summary>Rapor veri kümeleri, modül sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, DatasetDefinition Dataset)> Datasets =>
        Modules.SelectMany(m => m.Reports.Datasets.Select(d => datasets[d.Key]));

    public (string ModuleKey, DatasetDefinition Dataset)? FindDataset(string key) => datasets.TryGetValue(key, out var dataset) ? dataset : null;

    public IEnumerable<(string ModuleKey, ReportLink Link)> ReportLinks =>
        Modules.SelectMany(m => m.Reports.Links.Select(l => reportLinks[l.Key]));

    private void RegisterReporting()
    {
        foreach (var module in Modules)
        {
            foreach (var dataset in module.Reports.Datasets)
            {
                ValidateDataset(module.Key, dataset);
                if (!datasets.TryAdd(dataset.Key, (module.Key, dataset)))
                {
                    throw new InvalidOperationException($"Rapor veri kümesi iki kez tanımlı: '{dataset.Key}'.");
                }
            }

            foreach (var link in module.Reports.Links)
            {
                if (!link.Key.StartsWith(module.Key + ".", StringComparison.Ordinal) || link.Title.Length == 0 || link.Path.Length == 0
                    || !permissions.ContainsKey(link.Permission) || !reportLinks.TryAdd(link.Key, (module.Key, link)))
                {
                    throw new InvalidOperationException($"Geçersiz ya da tekrar eden rapor bağlantısı: '{link.Key}' ({module.Key}).");
                }
            }
        }

        // Hazır raporlar sütunlara başvurur; hepsi okunduktan sonra, çağıranın görebildiği her sütun varsayımıyla doğrulanır.
        foreach (var (moduleKey, dataset) in datasets.Values)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var columns = ReportColumnSet.Build(dataset, new HashSet<string>(), []);
            foreach (var ready in dataset.ReadyReports)
            {
                if (!ready.Key.StartsWith(moduleKey + ".", StringComparison.Ordinal) || ready.Title.Length == 0 || !keys.Add(ready.Key))
                {
                    throw new InvalidOperationException($"Geçersiz ya da tekrar eden hazır rapor: '{ready.Key}' ({dataset.Key}).");
                }

                var issues = ReportSpecValidator.Validate(ReportSpecValidator.Normalize(ready.Spec, columns), columns);
                if (issues.Count > 0)
                {
                    throw new InvalidOperationException($"Hazır rapor '{ready.Key}' geçersiz: {issues[0].Message}");
                }
            }
        }
    }

    private void ValidateDataset(string moduleKey, DatasetDefinition dataset)
    {
        var prefix = moduleKey + ".";
        if (!dataset.Key.StartsWith(prefix, StringComparison.Ordinal) || !dataset.EntityKey.StartsWith(prefix, StringComparison.Ordinal)
            || dataset.DisplayName.Length == 0 || dataset.Version < 1 || dataset.Columns.Count == 0)
        {
            throw new InvalidOperationException($"Geçersiz rapor veri kümesi: '{dataset.Key}' ({moduleKey}). Anahtarlar '{prefix}' ile başlamalı.");
        }

        if (dataset.AllowsCustomFields && !customizable.ContainsKey(dataset.EntityKey))
        {
            throw new InvalidOperationException($"'{dataset.Key}' özel alan kabul ediyor ama '{dataset.EntityKey}' özelleştirilebilir bir kayıt türü değil.");
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in dataset.Columns)
        {
            var bad = column.Field.Length == 0 || column.Label.Length == 0 || column.Use == ColumnUse.None || !fields.Add(column.Field)
                || RecordFieldNames.IsCustom(column.Field)
                || (column.Kind == DatasetColumnKind.Money && column.CurrencyField is null)
                || (column.Use.HasFlag(ColumnUse.Measure) && column.Kind is not (DatasetColumnKind.Number or DatasetColumnKind.Money))
                || (column.Use.HasFlag(ColumnUse.Group) && column.Kind is DatasetColumnKind.Number or DatasetColumnKind.Money)
                || (column.Kind == DatasetColumnKind.Choice && column.ChoiceList is null && (column.Options is not { Count: > 0 }))
                || (column.ChoiceList is { } list && !choiceLists.ContainsKey(list))
                || (column.Kind == DatasetColumnKind.Lookup && (column.LookupKey is null || !lookups.ContainsKey(column.LookupKey)));
            if (bad)
            {
                throw new InvalidOperationException($"Geçersiz veri kümesi sütunu: '{column.Field}' ({dataset.Key}).");
            }
        }

        if (dataset.DefaultColumns.Any(c => dataset.Columns.All(x => x.Field != c || !x.Use.HasFlag(ColumnUse.Show))))
        {
            throw new InvalidOperationException($"'{dataset.Key}' varsayılan sütunları, gösterilebilir bir sütuna başvurmuyor.");
        }
    }
}
