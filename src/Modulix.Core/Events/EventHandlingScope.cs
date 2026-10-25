using Modulix.Core.Contracts.Events;

namespace Modulix.Core.Events;

/// <summary>
/// Kapsamda işlenen olay (ADR-0007). Dağıtıcı, aboneyi çalıştırdığı kapsamda doldurur; kayıt boru hattı abonenin
/// yaptığı değişikliği buna bağlar: kaynak arka plan işi, <c>CausationId</c> tetikleyen olay, <c>CorrelationId</c> aynı,
/// derinlik bir fazla. İstek kapsamında boştur.
/// </summary>
public sealed class EventHandlingScope
{
    /// <summary>Derinliği bunu aşan olay yazılır ama abonelere dağıtılmaz: aboneler birbirini sonsuza kadar tetikleyemez.</summary>
    public const int MaxDepth = 5;

    public EventEnvelope? Event { get; private set; }

    /// <summary>Çalışan aboneliğin anahtarı (ör. "core.assignment-email").</summary>
    public string? Subscription { get; private set; }

    public void Begin(EventEnvelope envelope, string subscription)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        if (Event is not null)
        {
            throw new InvalidOperationException("Kapsamda zaten bir olay işleniyor.");
        }

        Event = envelope;
        Subscription = subscription;
    }
}
