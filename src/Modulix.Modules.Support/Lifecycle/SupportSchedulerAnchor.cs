using Modulix.Core.Contracts.Events;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>"support.scheduler" kimliğinin izin çapası (zamanlanmış işin izinlerini tanımlar); olay işlemez.</summary>
internal sealed class SupportSchedulerAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>"support.survey" kimliğinin izin çapası (herkese açık anket sayfası sistem adına çalışır); olay işlemez.</summary>
internal sealed class SupportSurveyAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
