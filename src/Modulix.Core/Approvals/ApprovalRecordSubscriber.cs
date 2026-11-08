using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Approvals;

/// <summary>
/// Kayda bağlı onayın bütünlüğü (ADR-0027 Dilim 4c, Ç6a.2): onaya gönderilmiş kaydın tutarı ya da para birimi değişirse bekleyen talep
/// geçersizleşir; verilmiş onay değişen tutara taşınmaz. Hassas alanın değeri aboneye gizli gelse de adı <see cref="EventEnvelope.HiddenFields"/>
/// içindedir, bu yüzden değişiklik yine yakalanır.
/// </summary>
public sealed class ApprovalRecordSubscriber(IApprovalService approvals) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        List<string> changed = [.. envelope.Payload.Select(p => p.Key), .. envelope.HiddenFields];
        if (changed.Count > 0)
        {
            await approvals.InvalidateForRecordAsync(new RecordRef(envelope.EntityKey, envelope.RecordId), changed, cancellationToken);
        }
    }
}
