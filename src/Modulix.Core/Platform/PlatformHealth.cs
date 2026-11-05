namespace Modulix.Core.Platform;

/// <summary>Kuyruktaki işler. <see cref="OldestPendingAt"/>: en eski bekleyenin oluşma anı (bekleme süresini gösterir).</summary>
public sealed record QueueHealth(int Pending, int Failed, DateTimeOffset? OldestPendingAt);

/// <summary>Olay kuyruğu (ADR-0007): <see cref="Held"/> sırası beklediği için tutulan teslimlerdir.</summary>
public sealed record EventQueueHealth(int Pending, int Held, int Failed, DateTimeOffset? OldestUndispatchedAt, DateTimeOffset? OldestPendingAt);

/// <summary>Bir pencerede tamamlanan işlerin sonucu; hata oranı = başarısız / (başarılı + başarısız).</summary>
public sealed record OutcomeStats(int Succeeded, int Failed)
{
    public double FailureRate => Succeeded + Failed == 0 ? 0 : (double)Failed / (Succeeded + Failed);
}

/// <summary>Kullanım özeti: yalnızca platform tablolarından sayılır (firma verisi okunmaz).</summary>
public sealed record UsageSummary(int OperationalTenants, int ActiveMembers, int SignedInTenants7Days, int SignedInTenants30Days, int SessionsSeen24Hours);

/// <summary>Son girişi 30 günden eski (ya da hiç giriş yapılmamış) açık firma.</summary>
public sealed record InactiveTenant(Guid TenantId, string Name, string Slug, DateTimeOffset? LastSignInAt);

public sealed record SecurityEventCount(string Action, int Count);

public sealed record VersionInfo(string Application, string Runtime, string? LastMigration, int PendingMigrations);

/// <summary>
/// Platform sağlık ve kullanım raporu (ADR-0028 Dilim 6, Y4.3). Yalnızca sayılar, zamanlar ve firma adı/kısa adı içerir: ödeme bilgisi,
/// firma kayıtları, dosyalar, e-posta içeriği ya da alıcı adresi yoktur, bu yüzden Destek rolü de dahil her platform rolüne açıktır.
/// </summary>
public sealed record PlatformHealthReport(
    DateTimeOffset GeneratedAt,
    VersionInfo Version,
    QueueHealth Email,
    EventQueueHealth Events,
    OutcomeStats EventDeliveries24Hours,
    OutcomeStats Emails24Hours,
    UsageSummary Usage,
    IReadOnlyList<InactiveTenant> InactiveTenants,
    IReadOnlyList<SecurityEventCount> SecurityEvents7Days);

public interface IPlatformHealth
{
    Task<PlatformHealthReport> GetAsync(CancellationToken cancellationToken);
}
