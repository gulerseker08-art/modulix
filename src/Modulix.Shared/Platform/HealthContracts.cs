namespace Modulix.Shared.Platform;

// Platform sağlık ve kullanım raporu (ADR-0028 Dilim 6): GET /api/platform/health. Yalnızca sayı, zaman ve firma adı içerir.

public sealed record QueueHealthDto(int Pending, int Failed, DateTimeOffset? OldestPendingAt);

public sealed record EventQueueHealthDto(int Pending, int Held, int Failed, DateTimeOffset? OldestUndispatchedAt, DateTimeOffset? OldestPendingAt);

/// <summary><see cref="FailureRate"/>: başarısız / (başarılı + başarısız); iş yoksa 0.</summary>
public sealed record OutcomeStatsDto(int Succeeded, int Failed, double FailureRate);

public sealed record UsageSummaryDto(int OperationalTenants, int ActiveMembers, int SignedInTenants7Days, int SignedInTenants30Days, int SessionsSeen24Hours);

public sealed record InactiveTenantDto(Guid TenantId, string Name, string Slug, DateTimeOffset? LastSignInAt);

public sealed record SecurityEventCountDto(string Action, int Count);

public sealed record VersionInfoDto(string Application, string Runtime, string? LastMigration, int PendingMigrations);

public sealed record PlatformHealthResponse(
    DateTimeOffset GeneratedAt,
    VersionInfoDto Version,
    QueueHealthDto Email,
    EventQueueHealthDto Events,
    OutcomeStatsDto EventDeliveries24Hours,
    OutcomeStatsDto Emails24Hours,
    UsageSummaryDto Usage,
    IReadOnlyList<InactiveTenantDto> InactiveTenants,
    IReadOnlyList<SecurityEventCountDto> SecurityEvents7Days);
