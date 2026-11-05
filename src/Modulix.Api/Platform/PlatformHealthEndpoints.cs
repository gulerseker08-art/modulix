using Modulix.Core.Platform;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Platform sağlık ve kullanım raporu (ADR-0028 Dilim 6, Y4.3): GET /api/platform/health. Her platform rolüne açıktır (okuma); rapor
/// ödeme bilgisi, firma kaydı, dosya ya da e-posta içeriği taşımaz, yalnızca sayı, zaman ve firma adı verir.
/// </summary>
public static class PlatformHealthEndpoints
{
    public static IEndpointRouteBuilder MapPlatformHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/platform")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform")
            .MapGet("/health", async (IPlatformHealth health, CancellationToken ct) => ToDto(await health.GetAsync(ct)));
        return app;
    }

    private static OutcomeStatsDto ToDto(OutcomeStats stats) => new(stats.Succeeded, stats.Failed, stats.FailureRate);

    private static PlatformHealthResponse ToDto(PlatformHealthReport report) => new(
        report.GeneratedAt,
        new VersionInfoDto(report.Version.Application, report.Version.Runtime, report.Version.LastMigration, report.Version.PendingMigrations),
        new QueueHealthDto(report.Email.Pending, report.Email.Failed, report.Email.OldestPendingAt),
        new EventQueueHealthDto(report.Events.Pending, report.Events.Held, report.Events.Failed, report.Events.OldestUndispatchedAt, report.Events.OldestPendingAt),
        ToDto(report.EventDeliveries24Hours),
        ToDto(report.Emails24Hours),
        new UsageSummaryDto(
            report.Usage.OperationalTenants, report.Usage.ActiveMembers, report.Usage.SignedInTenants7Days, report.Usage.SignedInTenants30Days,
            report.Usage.SessionsSeen24Hours),
        [.. report.InactiveTenants.Select(t => new InactiveTenantDto(t.TenantId, t.Name, t.Slug, t.LastSignInAt))],
        [.. report.SecurityEvents7Days.Select(e => new SecurityEventCountDto(e.Action, e.Count))]);
}
