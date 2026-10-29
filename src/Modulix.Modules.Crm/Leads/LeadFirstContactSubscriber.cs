using System.Globalization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Tamamlanan Lead aktivitesinden "ilk temas"ı türetir (<c>crm.lead-first-contact</c>). Yalnızca gerçekleşen temaslar sayılır
/// (<see cref="LeadRules.CountsAsContact"/>): planlanmış arama, görev ve not temas değildir. Tek ve sınırlı otomasyon budur:
/// aktivite tamamlamak Lead'i nitelendirmez, elemez ya da dönüştürmez. Teslim en az bir kezdir; <see cref="ILeadWorkflow.RecordContactAsync"/>
/// idempotent olduğu için tekrar teslimde ikinci bir değişiklik, audit ya da olay oluşmaz.
/// </summary>
internal sealed class LeadFirstContactSubscriber(IRecordPipeline pipeline, ILeadWorkflow workflow) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var activity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Activity, envelope.RecordId), cancellationToken);
        if (!activity.IsSuccess || activity.Value!.Values is not { } values)
        {
            return;
        }

        string? Text(string name) => values[name]?.GetValue<string>();
        if (!Guid.TryParse(Text("leadId"), out var leadId)
            || Text("completedAt") is not { } completed
            || !LeadRules.CountsAsContact(Text("type") ?? string.Empty, Text("outcome"), completed: true))
        {
            return;
        }

        // Lead kapsam dışı/silinmişse ya da dönüşmüş/elenmişse tepki verilmez (hata değil): yalnızca açık Lead'in teması işlenir.
        var lead = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Lead, leadId), cancellationToken);
        if (!lead.IsSuccess || !LeadStatus.IsOpen(lead.Value!.Values["status"]?.GetValue<string>() ?? string.Empty))
        {
            return;
        }

        await workflow.RecordContactAsync(leadId, DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture), cancellationToken);
    }
}
