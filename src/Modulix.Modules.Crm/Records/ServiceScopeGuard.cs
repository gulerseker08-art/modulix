using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sectors;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Hizmet kapsamı (ADR-0029 Dilim F) sunucu doğrulaması: kayda yeni eklenen kodlar faaliyet kataloğunda bulunan, aktif ve "Faaliyet"
/// türünde düğümler olmalıdır. Kayıtta zaten duran kodlar (sonradan pasife alınmış ya da katalogdan düşmüş olsa da) denetlenmez;
/// böylece eski kayıt başka bir alan düzenlenirken hata vermez.
/// </summary>
internal static class ServiceScopeGuard
{
    public static async Task AddErrorsAsync(
        List<RecordFieldError> errors, CrmStoreContext context, string field, string? value, string? stored, CancellationToken cancellationToken)
    {
        var added = ActivityCodeList.Split(value).Except(ActivityCodeList.Split(stored), StringComparer.Ordinal).ToList();
        if (added.Count == 0 || context.ActivityCodes is not { } catalog)
        {
            return;
        }

        var known = (await catalog.FindAsync(added, cancellationToken)).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var unknown = added.Where(c => !known.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
        {
            errors.Add(new RecordFieldError(field, $"Katalogda olmayan faaliyet: {string.Join(", ", unknown)}."));
        }

        var notActivities = added.Where(c => known.TryGetValue(c, out var info) && info.Kind != ActivityCodeKinds.Activity).ToList();
        if (notActivities.Count > 0)
        {
            errors.Add(new RecordFieldError(field, $"Hizmet kapsamı için sektör veya grup değil, faaliyet seçilmeli: {string.Join(", ", notActivities)}."));
        }

        var inactive = added.Where(c => known.TryGetValue(c, out var info) && info.Kind == ActivityCodeKinds.Activity && !info.IsActive).ToList();
        if (inactive.Count > 0)
        {
            errors.Add(new RecordFieldError(field, $"Pasif faaliyet eklenemez: {string.Join(", ", inactive)}."));
        }
    }
}
