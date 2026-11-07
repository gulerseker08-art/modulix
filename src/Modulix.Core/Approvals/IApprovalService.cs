namespace Modulix.Core.Approvals;

/// <summary>İş kuralı ihlali: kullanıcıya gösterilir (422).</summary>
public sealed class ApprovalException(string message) : Exception(message);

/// <summary>Eşzamanlı değişiklik (409): kayıt başkası tarafından değiştirildi.</summary>
public sealed class ApprovalConflictException(string message) : Exception(message);

/// <summary>Kullanıcı bu işlemi yapamaz (403): izni olsa bile bu adımın onaycısı değildir.</summary>
public sealed class ApprovalForbiddenException(string message) : Exception(message);

public sealed record ApprovalDefinitionInput(string Name, string? Description, string? Currency, IReadOnlyList<ApprovalStepDefinition> Steps);

public sealed record ApprovalRequestInput(Guid DefinitionId, string Title, string? Description, decimal? Amount, string? Currency);

public enum ApprovalRequestView
{
    /// <summary>Kararımı bekleyenler.</summary>
    Waiting = 1,

    /// <summary>Açtığım talepler.</summary>
    Mine = 2,

    /// <summary>Görebildiğim bütün talepler.</summary>
    All = 3,
}

public sealed record ApprovalRequestQuery(
    ApprovalRequestView View, ApprovalRequestStatus? Status, Guid? DefinitionId, string? Search, string? Sort, int Page, int PageSize)
{
    public const int MaxPageSize = 100;
}

public sealed record ApprovalStepDetail(
    Guid Id, int Order, string Name, Guid? ApproverUserId, Guid? ApproverRoleId, string ApproverName, ApprovalStepStatus Status,
    DateTimeOffset? ActivatedAt, DateTimeOffset? DueAt, bool IsOverdue, bool IsOrphaned, Guid? DecidedBy, string? DecidedByName,
    DateTimeOffset? DecidedAt, string? Comment);

public sealed record ApprovalRequestDetail(
    Guid Id, Guid DefinitionId, string DefinitionName, int DefinitionRevision, string Title, string? Description, decimal? Amount, string? Currency,
    Guid RequesterId, string RequesterName, ApprovalRequestStatus Status, DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, long Version,
    IReadOnlyList<ApprovalStepDetail> Steps, bool CanDecide, bool CanWithdraw);

public sealed record ApprovalRequestPage(IReadOnlyList<ApprovalRequestDetail> Items, int Page, int PageSize, int TotalCount);

public sealed record ApprovalDayCount(DateOnly Day, int Approved, int Rejected);

public sealed record ApprovalProcessCount(string Name, int Open);

/// <summary>Pano özeti: yalnızca kullanıcının görebildiği talepler sayılır.</summary>
public sealed record ApprovalOverview(
    int WaitingForMe, int Overdue, int MyOpen, int Orphaned, int ApprovedLast30Days, int RejectedLast30Days, decimal? AverageDecisionHours,
    IReadOnlyList<ApprovalDayCount> Trend, IReadOnlyList<ApprovalProcessCount> OpenByProcess);

public sealed record ApproverOption(Guid Id, string Name);

public sealed record ApproverOptions(IReadOnlyList<ApproverOption> People, IReadOnlyList<ApproverOption> Roles);

/// <summary>
/// Genel onay (ADR-0027 Dilim 4a). Mevcut teklif ve sözleşme onaylarının yerine geçmez. Her yazma <c>core.approval-definition</c> ya da
/// <c>core.approval-request</c> kaydı olarak denetime yazılır. <c>seeAll</c> parametreleri çağıranın <c>core.approvals.manage</c>
/// iznini taşır; görünürlük sorgu seviyesinde uygulanır.
/// </summary>
public interface IApprovalService
{
    Task<IReadOnlyList<ApprovalDefinition>> ListDefinitionsAsync(bool activeOnly, CancellationToken cancellationToken);

    Task<ApprovalDefinition> CreateDefinitionAsync(ApprovalDefinitionInput input, CancellationToken cancellationToken);

    /// <summary>Tanım yoksa null.</summary>
    Task<ApprovalDefinition?> UpdateDefinitionAsync(Guid id, ApprovalDefinitionInput input, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Tanım yoksa null. Kapatılan tanımla yeni talep açılamaz; açık talepler sürer.</summary>
    Task<ApprovalDefinition?> SetDefinitionActiveAsync(Guid id, bool active, CancellationToken cancellationToken);

    Task<ApproverOptions> ApproverOptionsAsync(CancellationToken cancellationToken);

    Task<ApprovalOverview> OverviewAsync(bool seeAll, CancellationToken cancellationToken);

    Task<ApprovalRequestPage> ListRequestsAsync(ApprovalRequestQuery query, bool seeAll, CancellationToken cancellationToken);

    /// <summary>Talep yoksa ya da kullanıcı göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> GetRequestAsync(Guid id, bool seeAll, CancellationToken cancellationToken);

    Task<ApprovalRequestDetail> OpenRequestAsync(ApprovalRequestInput input, CancellationToken cancellationToken);

    /// <summary>Kullanıcının karar verebildiği etkin adımı onaylar ya da reddeder (ret notu zorunlu). Talep yoksa ya da göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> DecideAsync(Guid id, bool approve, string? comment, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Talep sahibi açık talebi geri çeker. Talep yoksa ya da göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> WithdrawAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Etkin adımın onaycısını değiştirir (yönetici). Talep yoksa null.</summary>
    Task<ApprovalRequestDetail?> ReassignAsync(Guid id, Guid stepId, Guid? userId, Guid? roleId, long expectedVersion, CancellationToken cancellationToken);
}
