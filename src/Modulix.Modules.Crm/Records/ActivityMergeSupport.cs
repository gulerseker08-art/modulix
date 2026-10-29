using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Mükerrer firma/kişi birleştirildiğinde aktivite geçmişini güvenle taşır (ADR-0013, ADR-0015): doğrudan sütunlar kayıt
/// depolarında taşınır; burada genel ilişki satırları ve katılımcı kişiler taşınır. Hedef zaten aynı aktiviteye bağlıysa mükerrer
/// satır kaldırılır (benzersizlik korunur, aktivite yine tek görünür); hiçbir aktivite silinmez.
/// </summary>
internal static class ActivityMergeSupport
{
    public static async Task MoveLinksAsync(
        IModuleData data, string entityKey, Guid sourceId, Guid targetId, List<MergeImpact> impacts, CancellationToken cancellationToken)
    {
        var links = await data.Set<ActivityLink>().Where(l => l.EntityKey == entityKey && l.RecordId == sourceId).ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return;
        }

        var activityIds = links.Select(l => l.ActivityId).ToList();
        var already = (await data.Set<ActivityLink>().AsNoTracking()
            .Where(l => l.EntityKey == entityKey && l.RecordId == targetId && activityIds.Contains(l.ActivityId))
            .Select(l => l.ActivityId).ToListAsync(cancellationToken)).ToHashSet();

        foreach (var link in links)
        {
            if (already.Contains(link.ActivityId))
            {
                data.Set<ActivityLink>().Remove(link);
            }
            else
            {
                link.RecordId = targetId;
                link.Touch();
            }

            MergeSupport.AddImpact(impacts, "Aktivite ilişkisi taşınır", 1);
        }
    }

    public static async Task MoveParticipantsAsync(
        IModuleData data, Guid sourceContactId, Guid targetContactId, List<MergeImpact> impacts, CancellationToken cancellationToken)
    {
        var participants = await data.Set<ActivityParticipant>().Where(p => p.ContactId == sourceContactId).ToListAsync(cancellationToken);
        if (participants.Count == 0)
        {
            return;
        }

        var activityIds = participants.Select(p => p.ActivityId).ToList();
        var already = (await data.Set<ActivityParticipant>().AsNoTracking()
            .Where(p => p.ContactId == targetContactId && activityIds.Contains(p.ActivityId))
            .Select(p => p.ActivityId).ToListAsync(cancellationToken)).ToHashSet();

        foreach (var participant in participants)
        {
            if (already.Contains(participant.ActivityId))
            {
                data.Set<ActivityParticipant>().Remove(participant);
            }
            else
            {
                participant.ContactId = targetContactId;
                participant.Touch();
            }

            MergeSupport.AddImpact(impacts, "Aktivite katılımcısı taşınır", 1);
        }
    }
}
