namespace Modulix.Shared.Approvals;

public sealed record ApprovalStepDefinitionDto(
    string Name, int Order, Guid? ApproverUserId, Guid? ApproverRoleId, decimal? MinAmount, int? DueHours, Guid? EscalateToUserId = null, Guid? EscalateToRoleId = null);

public sealed record ApprovalDefinitionDto(
    Guid Id, string Name, string? Description, string? Currency, bool IsActive, int Revision, long Version, IReadOnlyList<ApprovalStepDefinitionDto> Steps);

public sealed record SaveApprovalDefinitionRequest(
    string Name, string? Description, string? Currency, IReadOnlyList<ApprovalStepDefinitionDto> Steps, long? ExpectedVersion = null);

public sealed record SetApprovalDefinitionActiveRequest(bool IsActive);

public sealed record ApproverOptionDto(Guid Id, string Name);

public sealed record ApproverOptionsDto(IReadOnlyList<ApproverOptionDto> People, IReadOnlyList<ApproverOptionDto> Roles);

public sealed record ApprovalStepDto(
    Guid Id, int Order, string Name, Guid? ApproverUserId, Guid? ApproverRoleId, string ApproverName, string Status, DateTimeOffset? ActivatedAt,
    DateTimeOffset? DueAt, bool IsOverdue, bool IsOrphaned, string? DecidedByName, DateTimeOffset? DecidedAt, string? Comment,
    string? OnBehalfOfName = null, string? EscalationName = null, bool IsEscalated = false);

public sealed record ApprovalRequestDto(
    Guid Id, Guid DefinitionId, string DefinitionName, int DefinitionRevision, string Title, string? Description, decimal? Amount, string? Currency,
    Guid RequesterId, string RequesterName, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, long Version,
    IReadOnlyList<ApprovalStepDto> Steps, bool CanDecide, bool CanWithdraw);

public sealed record ApprovalRequestPageDto(IReadOnlyList<ApprovalRequestDto> Items, int Page, int PageSize, int TotalCount);

public sealed record OpenApprovalRequest(Guid DefinitionId, string Title, string? Description, decimal? Amount, string? Currency);

public sealed record DecideApprovalRequest(string? Comment, long ExpectedVersion);

public sealed record ReassignApprovalStepRequest(Guid StepId, Guid? UserId, Guid? RoleId, long ExpectedVersion);

public sealed record ApprovalDelegationDto(
    Guid Id, Guid PrincipalUserId, string PrincipalName, Guid DelegateUserId, string DelegateName, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Reason, bool IsActive, DateTimeOffset? RevokedAt);

public sealed record CreateApprovalDelegationRequest(Guid? PrincipalUserId, Guid DelegateUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record ApprovalDayCountDto(DateOnly Day, int Approved, int Rejected);

public sealed record ApprovalProcessCountDto(string Name, int Open);

public sealed record ApprovalOverviewDto(
    int WaitingForMe, int Overdue, int MyOpen, int Orphaned, int ApprovedLast30Days, int RejectedLast30Days, decimal? AverageDecisionHours,
    IReadOnlyList<ApprovalDayCountDto> Trend, IReadOnlyList<ApprovalProcessCountDto> OpenByProcess);
