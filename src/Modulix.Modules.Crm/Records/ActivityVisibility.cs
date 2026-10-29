using System.Linq.Expressions;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Aktivite görünürlük kuralları (ADR-0015). Veri kapsamı (Y8) sorumluya göredir; ek olarak katılımcı olduğu aktiviteyi herkes görür.
/// Özel aktivitenin ayrıntısı yalnızca sorumlu, oluşturan, katılımcılar ve <c>crm.activities.view-private</c> sahibine açıktır;
/// diğerleri yalnızca meşguliyet görür. Liste, arama, ilişki süzgeçleri ve ilişkili kayıt tabloları aynı kuralı kullanır.
/// </summary>
internal static class ActivityVisibility
{
    public static Task<bool> CanViewPrivateAsync(CrmStoreContext context, CancellationToken cancellationToken) =>
        context.Permissions.HasPermissionAsync(CrmModule.Permissions.ActivitiesViewPrivate, cancellationToken);

    /// <summary>Çağıranın ayrıntıyı göreceği aktiviteler: özel olmayanlar ya da çağıranın sorumlu/oluşturan/katılımcı olduğu aktiviteler.</summary>
    public static async Task<IQueryable<Activity>> DetailVisibleAsync(
        IQueryable<Activity> source, CrmStoreContext context, CancellationToken cancellationToken)
    {
        if (await CanViewPrivateAsync(context, cancellationToken))
        {
            return source;
        }

        var me = context.CurrentUserId ?? Guid.Empty;
        var participants = context.Data.Set<ActivityParticipant>();
        return source.Where(a => !a.IsPrivate || a.OwnerId == me || a.CreatedBy == me
            || participants.Any(p => p.ActivityId == a.Id && p.UserId == me));
    }

    /// <summary>Çağıranın sorumlu olduğu ya da katılımcısı olduğu aktiviteler (veri kapsamı genişlemesi).</summary>
    public static Expression<Func<Activity, bool>> InScope(CrmStoreContext context, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var me = context.CurrentUserId ?? Guid.Empty;
        var participants = context.Data.Set<ActivityParticipant>();
        return a => ids.Contains(a.OwnerId) || participants.Any(p => p.ActivityId == a.Id && p.UserId == me);
    }

    /// <summary>
    /// Bir kayda bağlı aktiviteler (zaman akışı). Doğrudan sütunlar ve ilişki satırları tek koşulda birleşir, bu yüzden aynı aktivite
    /// birden fazla ilişkiden dolayı çoğalmaz. Dönüşmüş Lead'in aktiviteleri, dönüşümle oluşan firma/kişi/fırsatın akışında da görünür
    /// (açık takipler dönüşümle kaybolmaz). <paramref name="rollUp"/>: firma akışı kişilerinin ve fırsatlarının aktivitelerini de içerir.
    /// </summary>
    public static Expression<Func<Activity, bool>>? Related(CrmStoreContext context, string entityKey, Guid id, bool rollUp)
    {
        var links = context.Data.Set<ActivityLink>();
        var leads = context.Data.Set<Lead>();
        var contacts = context.Data.Set<CompanyContact>();
        var opportunities = context.Data.Set<Opportunity>();

        return entityKey switch
        {
            CrmModule.Entities.Company when rollUp => a =>
                a.CompanyId == id
                || (a.ContactId != null && contacts.Any(c => c.CompanyId == id && c.ContactId == a.ContactId))
                || (a.OpportunityId != null && opportunities.Any(o => o.CompanyId == id && o.Id == a.OpportunityId))
                || (a.LeadId != null && leads.Any(l => l.Id == a.LeadId && l.ConvertedCompanyId == id))
                || links.Any(l => l.ActivityId == a.Id && ((l.EntityKey == entityKey && l.RecordId == id)
                    || (l.EntityKey == CrmModule.Entities.Opportunity && opportunities.Any(o => o.CompanyId == id && o.Id == l.RecordId)))),
            CrmModule.Entities.Company => a =>
                a.CompanyId == id
                || (a.LeadId != null && leads.Any(l => l.Id == a.LeadId && l.ConvertedCompanyId == id))
                || links.Any(l => l.ActivityId == a.Id && l.EntityKey == entityKey && l.RecordId == id),
            CrmModule.Entities.Contact => a =>
                a.ContactId == id
                || (a.LeadId != null && leads.Any(l => l.Id == a.LeadId && l.ConvertedContactId == id))
                || links.Any(l => l.ActivityId == a.Id && l.EntityKey == entityKey && l.RecordId == id),
            CrmModule.Entities.Opportunity => a =>
                a.OpportunityId == id
                || (a.LeadId != null && leads.Any(l => l.Id == a.LeadId && l.ConvertedOpportunityId == id))
                || links.Any(l => l.ActivityId == a.Id && l.EntityKey == entityKey && l.RecordId == id),
            CrmModule.Entities.Lead => a =>
                a.LeadId == id || links.Any(l => l.ActivityId == a.Id && l.EntityKey == entityKey && l.RecordId == id),
            _ => entityKey.Length <= ActivityLink.EntityKeyMaxLength && context.LinkTargets.Any(t => t.EntityKey == entityKey)
                ? a => links.Any(l => l.ActivityId == a.Id && l.EntityKey == entityKey && l.RecordId == id)
                : null,
        };
    }
}
