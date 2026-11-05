using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Automation;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// "Aktiviteler" ilişkisinin çözümleyicisi (ADR-0027, Dilim 3): müşteri adayı ya da fırsata bağlı aktivitelerin sayısını tek sorguyla verir.
/// Bir aktivite kayda doğrudan sütunuyla (<c>leadId</c>/<c>opportunityId</c>) ya da aktivite ilişkisiyle (<c>ActivityLink</c>) bağlı olabilir; ikisi de sayılır
/// ve aynı aktivite bir kez sayılır. <b>Özel aktiviteler sayılmaz</b>: kural kimliği onları görmez, sayı onların varlığını sızdırmasın.
/// <para>
/// <c>Where</c> koşulu hedef alanlarına (bugün yalnızca aktivite durumu) bakar. Durum seçim alanı olduğundan her yaprak bir durum kümesine çevrilir
/// (eşit, eşit değil, içinde, içinde değil, dolu); VE kümelerin kesişimi, VEYA birleşimidir. Böylece koşul veritabanına tek <c>IN</c> süzgeciyle iner.
/// </para>
/// </summary>
internal sealed class CrmActivityRelationResolver(IModuleData data) : IAutomationRelationResolver
{
    private static readonly string[] AllStatuses = [.. CrmAutomation.ActivityStatusOptions.Select(o => o.Key)];

    public async Task<int> CountAsync(AutomationRelationQuery query, CancellationToken cancellationToken)
    {
        var entityKey = query.Relation switch
        {
            CrmAutomation.LeadActivities => CrmModule.Entities.Lead,
            CrmAutomation.OpportunityActivities => CrmModule.Entities.Opportunity,
            _ => throw new InvalidOperationException("Bilinmeyen ilişki."),
        };

        var statuses = query.Where is null ? AllStatuses.ToHashSet(StringComparer.Ordinal) : StatusesOf(query.Where);
        if (statuses.Count == 0)
        {
            return 0;
        }

        var id = query.SourceRecordId;
        var allowed = statuses.ToArray();
        var activities = data.Set<Activity>().AsNoTracking().Where(a => !a.IsPrivate && allowed.Contains(a.Status));
        var links = data.Set<ActivityLink>().AsNoTracking().Where(k => k.EntityKey == entityKey && k.RecordId == id);

        return entityKey == CrmModule.Entities.Lead
            ? await activities.CountAsync(a => a.LeadId == id || links.Any(k => k.ActivityId == a.Id), cancellationToken)
            : await activities.CountAsync(a => a.OpportunityId == id || links.Any(k => k.ActivityId == a.Id), cancellationToken);
    }

    private static HashSet<string> StatusesOf(ConditionNode node)
    {
        switch (node)
        {
            case ConditionGroup group:
                HashSet<string>? result = null;
                foreach (var item in group.Items)
                {
                    var part = StatusesOf(item);
                    if (result is null)
                    {
                        result = part;
                    }
                    else if (group.Logic == ConditionLogic.And)
                    {
                        result.IntersectWith(part);
                    }
                    else
                    {
                        result.UnionWith(part);
                    }
                }

                return result ?? [];

            case ConditionLeaf leaf when leaf.Field == CrmModule.Entities.Activity + ".status":
                return leaf.Operator switch
                {
                    AutomationOperator.Eq or AutomationOperator.In => [.. leaf.Values.Where(AllStatuses.Contains)],
                    AutomationOperator.Ne or AutomationOperator.NotIn => [.. AllStatuses.Except(leaf.Values)],
                    AutomationOperator.IsNotEmpty => [.. AllStatuses],
                    _ => [],
                };

            default:
                // Doğrulayıcı hedef alanı dışında bir şeye izin vermez; buraya gelirse hiçbir aktivite eşleşmez (yanlış sayma yerine sıfır).
                return [];
        }
    }
}
