using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Reporting;

/// <summary>Veri kümesinin çağıran için çözümlenmiş hali; <see cref="Columns"/> yalnızca <see cref="Access"/> uygunsa doludur.</summary>
public sealed record ResolvedDataset(
    string ModuleKey, DatasetDefinition Dataset, ReportRunStatus Access, ReportColumnSet? Columns, IReadOnlyList<ReportIssue> Issues);

/// <summary>
/// Veri kümesini çağıranın yetkisiyle çözümler: modül açık mı, kayıt türünü okuyabiliyor mu, hangi hassas alanları göremiyor, hangi özel
/// alanlar var. Rapor merkezindeki her karar (katalog, doğrulama, çalıştırma, dışa aktarma) buradan geçer; böylece yetki tek yerde uygulanır.
/// </summary>
public sealed class ReportColumnResolver(
    ModuleCatalog catalog, ICurrentAccess access, IRecordAccess recordAccess, ICustomFieldService customFields)
{
    private IReadOnlyList<CustomFieldInfo>? customCache;

    public async Task<ResolvedDataset?> ResolveAsync(string datasetKey, CancellationToken cancellationToken)
    {
        if (catalog.FindDataset(datasetKey) is not var (moduleKey, dataset))
        {
            return null;
        }

        if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
        {
            return new ResolvedDataset(moduleKey, dataset, ReportRunStatus.ModuleDisabled, null,
                [new ReportIssue(ReportIssueCode.ModuleDisabled, null, $"'{dataset.DisplayName}' için modül bu firmada etkin değil.")]);
        }

        if (!await recordAccess.CanAsync(dataset.EntityKey, RecordOperation.Read, cancellationToken))
        {
            return new ResolvedDataset(moduleKey, dataset, ReportRunStatus.Forbidden, null,
                [new ReportIssue(ReportIssueCode.NoAccess, null, $"'{dataset.DisplayName}' verisini görme yetkiniz yok.")]);
        }

        var permissions = await access.GetPermissionsAsync(cancellationToken);
        var hidden = catalog.HiddenFields(dataset.EntityKey, permissions);
        var customs = dataset.AllowsCustomFields ? await CustomFieldsAsync(cancellationToken) : [];
        return new ResolvedDataset(moduleKey, dataset, ReportRunStatus.Ok, ReportColumnSet.Build(dataset, hidden, customs), []);
    }

    /// <summary>Firmanın bütün özel alanları (arşivliler dahil), istek boyunca bir kez okunur.</summary>
    private async Task<IReadOnlyList<CustomFieldInfo>> CustomFieldsAsync(CancellationToken cancellationToken) =>
        customCache ??= await customFields.ListAsync(null, includeArchived: true, cancellationToken);
}
