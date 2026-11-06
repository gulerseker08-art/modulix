using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Automation;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM'in tarama kaynağı (ADR-0027, Dilim 3): müşteri adayı ve fırsat için "bugün hangi kayıtlar bu tarama tetikleyicisine uyuyor" sorusunu
/// yanıtlar. Yalnızca <b>kayıt kimlikleri</b> döner (kimliğe göre artan, anahtar tabanlı sayfa); koşul, alan yetkisi ve aksiyon motorda kuralın
/// kimliğiyle kayıt boru hattı üzerinden uygulanır.
/// <list type="bullet">
///   <item><b>Zamanlanmış</b> ve <b>hareketsizlik</b> taramaları yalnızca <b>açık</b> kayıtları kapsar (müşteri adayı: yeni, iletişimde, nitelikli, beslemede;
///   fırsat: açık, beklemede). Kapanmış kayıtlar için günlük tarama anlamsız satır üretirdi; kapanmış kayda tarihe göre kural yazılabilir.</item>
///   <item><b>Tarihe bağlı</b> tarama durumdan bağımsız, tarih alanı hedef güne düşen kayıtlardır (<see cref="CrmAutomation"/> tablosundaki alanlar).</item>
///   <item><b>Hareketsizlik</b>: açık kaydın aktivitelerinden (doğrudan bağ ya da aktivite ilişkisi) hiçbiri eşikten sonra güncellenmemiştir.
///   Özel aktiviteler sayılmaz (kural kimliği onları göremez).</item>
/// </list>
/// </summary>
internal sealed class CrmAutomationRecordSource(IModuleData data) : IAutomationRecordSource
{
    public async Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        var ids = query.EntityKey switch
        {
            CrmModule.Entities.Lead => await ScanLeadsAsync(query, cancellationToken),
            CrmModule.Entities.Opportunity => await ScanOpportunitiesAsync(query, cancellationToken),
            _ => throw new InvalidOperationException("Bilinmeyen kayıt türü."),
        };
        return new AutomationScanPage(ids);
    }

    private async Task<List<Guid>> ScanLeadsAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        var leads = data.Set<Lead>().AsNoTracking();
        var after = query.AfterRecordId;
        if (after is { } cursor)
        {
            leads = leads.Where(l => l.Id.CompareTo(cursor) > 0);
        }

        switch (query.Kind)
        {
            case AutomationScanKind.Scheduled:
                leads = leads.Where(l => LeadStatus.Open.Contains(l.Status));
                break;

            case AutomationScanKind.DateRelative:
                if (query.Field is null || query.TargetDate is not { } target
                    || !CrmAutomation.LeadDateColumns.TryGetValue(query.Field, out var column))
                {
                    throw new InvalidOperationException("Desteklenmeyen tarih alanı.");
                }

                leads = leads.Where(Matches(column, target));
                break;

            case AutomationScanKind.Inactivity:
                if (query.Relation != CrmAutomation.LeadActivities || query.InactiveBefore is not { } before)
                {
                    throw new InvalidOperationException("Desteklenmeyen ilişki.");
                }

                var activities = data.Set<Activity>().AsNoTracking().Where(a => !a.IsPrivate && a.UpdatedAt >= before);
                var links = data.Set<ActivityLink>().AsNoTracking().Where(k => k.EntityKey == CrmModule.Entities.Lead);
                leads = leads.Where(l => LeadStatus.Open.Contains(l.Status)
                    && !activities.Any(a => a.LeadId == l.Id)
                    && !links.Any(k => k.RecordId == l.Id && activities.Any(a => a.Id == k.ActivityId)));
                break;

            default:
                throw new InvalidOperationException("Desteklenmeyen tarama türü.");
        }

        return await leads.OrderBy(l => l.Id).Select(l => l.Id).Take(query.PageSize).ToListAsync(cancellationToken);
    }

    private async Task<List<Guid>> ScanOpportunitiesAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        var opportunities = data.Set<Opportunity>().AsNoTracking();
        var after = query.AfterRecordId;
        if (after is { } cursor)
        {
            opportunities = opportunities.Where(o => o.Id.CompareTo(cursor) > 0);
        }

        string[] open = [OpportunityStatus.Open, OpportunityStatus.OnHold];
        switch (query.Kind)
        {
            case AutomationScanKind.Scheduled:
                opportunities = opportunities.Where(o => open.Contains(o.Status));
                break;

            case AutomationScanKind.DateRelative:
                if (query.Field is null || query.TargetDate is not { } target
                    || !CrmAutomation.OpportunityDateColumns.TryGetValue(query.Field, out var column))
                {
                    throw new InvalidOperationException("Desteklenmeyen tarih alanı.");
                }

                opportunities = opportunities.Where(Matches(column, target));
                break;

            case AutomationScanKind.Inactivity:
                if (query.Relation != CrmAutomation.OpportunityActivities || query.InactiveBefore is not { } before)
                {
                    throw new InvalidOperationException("Desteklenmeyen ilişki.");
                }

                var activities = data.Set<Activity>().AsNoTracking().Where(a => !a.IsPrivate && a.UpdatedAt >= before);
                var links = data.Set<ActivityLink>().AsNoTracking().Where(k => k.EntityKey == CrmModule.Entities.Opportunity);
                opportunities = opportunities.Where(o => open.Contains(o.Status)
                    && !activities.Any(a => a.OpportunityId == o.Id)
                    && !links.Any(k => k.RecordId == o.Id && activities.Any(a => a.Id == k.ActivityId)));
                break;

            default:
                throw new InvalidOperationException("Desteklenmeyen tarama türü.");
        }

        return await opportunities.OrderBy(o => o.Id).Select(o => o.Id).Take(query.PageSize).ToListAsync(cancellationToken);
    }

    /// <summary>Tarih sütunu hedef güne eşit mi? Sütun ifadesi yeniden kullanılır, böylece sorgu çevrilebilir kalır.</summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> Matches<T>(System.Linq.Expressions.Expression<Func<T, DateOnly?>> column, DateOnly target)
    {
        var parameter = column.Parameters[0];
        var equals = System.Linq.Expressions.Expression.Equal(
            column.Body, System.Linq.Expressions.Expression.Constant(target, typeof(DateOnly?)));
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(equals, parameter);
    }
}
