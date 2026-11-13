using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>
/// Teklif kabul iş olayı (<c>crm.quote.accepted</c>, ADR-0039). İki kabul yolu da (kullanıcının kaydettiği kabul ve müşterinin sayfadan kabulü)
/// olayı durum değişikliğiyle <b>aynı işlemde</b> yayınlar: ikisi birlikte yazılır ya da hiçbiri. Ret, iptal ve süre dolumunun iş olayı yoktur.
/// </summary>
internal static class QuoteAcceptedEvent
{
    public const string Manual = "Manual";
    public const string Portal = "Portal";

    public static Task PublishAsync(IBusinessEventPublisher events, RecordData quote, string method, CancellationToken cancellationToken)
    {
        var payload = new JsonObject();
        foreach (var field in CrmModule.Events.QuoteAcceptedFields)
        {
            if (quote.Values.TryGetPropertyValue(field, out var value) && value is not null)
            {
                payload[field] = value.DeepClone();
            }
        }

        payload["acceptanceMethod"] = method;
        return events.PublishAsync(new BusinessEvent(CrmModule.Events.QuoteAccepted, quote.Id, payload) { RecordVersion = quote.Version }, cancellationToken);
    }
}
