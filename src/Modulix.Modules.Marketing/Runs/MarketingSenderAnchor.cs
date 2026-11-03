using Modulix.Core.Contracts.Events;

namespace Modulix.Modules.Marketing.Runs;

/// <summary>"marketing.sender" kimliğinin izin çapası (gönderim işçisinin izinlerini tanımlar); olay işlemez.</summary>
internal sealed class MarketingSenderAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
