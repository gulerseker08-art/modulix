using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Reporting;

/// <summary>
/// <see cref="IReportCatalog"/>: yalnızca etkin modüllerin veri kümeleri; sütunlar çağıranın yetkisine göre süzülür (hassas alan izni yoksa sütun
/// yoktur), özel alanlar eklenir, seçenekler firmanın güncel etiketleriyle gelir. Okuma yetkisi olmayan veri kümesi nedeniyle birlikte listelenir
/// ama kullanılamaz (kullanıcı neden göremediğini bilsin).
/// </summary>
public sealed class ReportCatalogService(
    ModuleCatalog catalog, ReportColumnResolver resolver, ICurrentAccess access, IChoiceLists choiceLists, ITenantMembers members) : IReportCatalog
{
    public async Task<ReportCatalogView> GetAsync(CancellationToken cancellationToken)
    {
        var datasets = new List<CatalogDataset>();
        foreach (var (moduleKey, dataset) in catalog.Datasets)
        {
            if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
            {
                continue;
            }

            var resolved = await resolver.ResolveAsync(dataset.Key, cancellationToken);
            if (resolved?.Columns is not { } columns)
            {
                datasets.Add(new CatalogDataset(
                    dataset.Key, moduleKey, dataset.DisplayName, dataset.Version, false,
                    resolved?.Issues.FirstOrDefault()?.Message ?? "Bu veri kümesini görme yetkiniz yok.", [], [], [], dataset.RecordPath));
                continue;
            }

            var catalogColumns = new List<CatalogColumn>();
            foreach (var column in columns.Columns)
            {
                catalogColumns.Add(new CatalogColumn(
                    column.Field, column.Label, column.Kind, column.Use, RecordFieldNames.IsCustom(column.Field), column.CurrencyField,
                    await OptionsAsync(column, cancellationToken)));
            }

            var available = columns.Columns.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);
            var ready = dataset.ReadyReports.Where(r => SpecUsesOnly(r.Spec, available)).ToList();
            datasets.Add(new CatalogDataset(
                dataset.Key, moduleKey, dataset.DisplayName, dataset.Version, true, null, catalogColumns,
                [.. dataset.DefaultColumns.Where(available.Contains)], ready, dataset.RecordPath));
        }

        var links = new List<CatalogLink>();
        var granted = await access.GetPermissionsAsync(cancellationToken);
        foreach (var (moduleKey, link) in catalog.ReportLinks)
        {
            if (granted.Contains(link.Permission) && await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
            {
                links.Add(new CatalogLink(link.Key, moduleKey, link.Title, link.Description, link.Path));
            }
        }

        return new ReportCatalogView(datasets, links);
    }

    /// <summary>Hazır rapor, çağıranın göremediği (hassas) bir sütuna başvuruyorsa listelenmez.</summary>
    private static bool SpecUsesOnly(ReportSpec spec, HashSet<string> available) =>
        spec.Columns.All(available.Contains)
        && spec.Filters.All(f => available.Contains(f.Field))
        && spec.Groups.All(g => available.Contains(g.Field))
        && spec.Measures.All(m => m.Field is null || available.Contains(m.Field));

    private async Task<IReadOnlyList<DatasetOption>?> OptionsAsync(DatasetColumn column, CancellationToken cancellationToken)
    {
        if (column.Kind == DatasetColumnKind.User)
        {
            // Kullanıcı sütunu: seçenekler firmanın etkin üyeleridir (süzgeç değeri kullanıcı kimliğidir).
            return [.. (await members.ListActiveAsync(cancellationToken)).OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(m => new DatasetOption(m.UserId.ToString(), m.DisplayName))];
        }

        if (column.Kind != DatasetColumnKind.Choice)
        {
            return null;
        }

        if (column.ChoiceList is not { } listKey)
        {
            return column.Options;
        }

        var list = await choiceLists.GetAsync(listKey, cancellationToken);
        return [.. list.Values.OrderByDescending(v => v.Active).Select(v => new DatasetOption(v.Key, v.Active ? v.Label : $"{v.Label} (pasif)"))];
    }
}
