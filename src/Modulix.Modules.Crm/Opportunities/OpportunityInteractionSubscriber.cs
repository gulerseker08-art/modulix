using System.Globalization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Leads;

namespace Modulix.Modules.Crm.Opportunities;

/// <summary>
/// Tamamlanan fırsat aktivitesinden "son müşteri etkileşimi"ni türetir (<c>crm.opportunity-interaction</c>). Yalnızca gerçekleşen
/// temaslar sayılır (<see cref="LeadRules.CountsAsContact"/>, Lead ile aynı kural): planlanmış arama, görev ve not etkileşim değildir.
/// "Son güncelleme" ile karıştırılmaz. Teslim en az bir kezdir; <see cref="IOpportunityWorkflow.RecordInteractionAsync"/> idempotenttir.
/// </summary>
internal sealed class OpportunityInteractionSubscriber(IRecordPipeline pipeline, IOpportunityWorkflow workflow) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var activity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Activity, envelope.RecordId), cancellationToken);
        if (!activity.IsSuccess)
        {
            return;
        }

        var values = activity.Value!.Values;
        string? Text(string name) => values[name]?.GetValue<string>();
        if (!Guid.TryParse(Text("opportunityId"), out var opportunityId)
            || Text("completedAt") is not { } completed
            || !LeadRules.CountsAsContact(Text("type") ?? string.Empty, Text("outcome"), completed: true))
        {
            return;
        }

        // Fırsat kapsam dışı ya da silinmişse tepki verilmez (hata değil).
        await workflow.RecordInteractionAsync(opportunityId, DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture), cancellationToken);
    }
}
