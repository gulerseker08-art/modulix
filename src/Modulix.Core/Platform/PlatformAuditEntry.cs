namespace Modulix.Core.Platform;

/// <summary>Platform denetim kaydındaki işlem anahtarları (ADR-0028).</summary>
public static class PlatformAuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantRenamed = "tenant.renamed";
    public const string TenantClosed = "tenant.closed";
    public const string TenantReopened = "tenant.reopened";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantActivated = "tenant.activated";
    public const string ModuleChanged = "tenant.module-changed";
    public const string AdministratorInvited = "tenant.administrator-invited";
    public const string ProfileApplied = "tenant.profile-applied";
    public const string UserTwoFactorReset = "user.two-factor-reset";
}

/// <summary>
/// Platform yöneticisinin işlemlerinin tenant'lar arası kaydı (ADR-0028). Platform geneli ve RLS dışıdır; ekleme-yalnızdır
/// (uygulama rolü UPDATE/DELETE yapamaz). Aynı işlem ayrıca ilgili firmanın kendi denetim kaydına yazılır (firma yöneticisi
/// kendi firmasında olanı görür); bu kayıt platform ekibinin firmalar arası görünümüdür.
///
/// Firma verisi değildir ve firma silinse de saklanır: bu yüzden sütun <c>TargetTenantId</c> adındadır ve yabancı anahtar
/// yoktur (tenant dışa aktarma/silme <c>TenantId</c> sütunlu tabloları süpürür; bu tablo kapsam dışıdır).
/// </summary>
public sealed class PlatformAuditEntry
{
    public const int ActionMaxLength = 100;
    public const int ReasonMaxLength = 500;
    public const int DetailsMaxLength = 4000;

    private PlatformAuditEntry()
    {
        // EF Core için
    }

    public PlatformAuditEntry(Guid actorUserId, string action, Guid? targetTenantId, string? reason, string? details, string? traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (action.Length > ActionMaxLength)
        {
            throw new ArgumentException($"İşlem anahtarı en fazla {ActionMaxLength} karakter olabilir.", nameof(action));
        }

        Id = Guid.CreateVersion7();
        OccurredAt = DateTimeOffset.UtcNow;
        ActorUserId = actorUserId;
        Action = action;
        TargetTenantId = targetTenantId;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim(), ReasonMaxLength);
        Details = string.IsNullOrWhiteSpace(details) ? null : Truncate(details, DetailsMaxLength);
        TraceId = traceId;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public Guid? TargetTenantId { get; private set; }

    /// <summary>İşlemin gerekçesi; askıya alma gibi kritik işlemlerde zorunludur.</summary>
    public string? Reason { get; private set; }

    /// <summary>Önce/sonra gibi ayrıntılar (JSON metni). Sır içermez.</summary>
    public string? Details { get; private set; }

    public string? TraceId { get; private set; }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
