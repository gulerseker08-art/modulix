using Modulix.Core.Contracts.Events;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>"support.scheduler" kimliğinin izin çapası (zamanlanmış işin izinlerini tanımlar); olay işlemez.</summary>
internal sealed class SupportSchedulerAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
