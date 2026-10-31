namespace Modulix.Shared.Contracts;

// ---- Dilim 5: iç onay (kurallar, adımlar, vekâlet, karar, iş listesi)

/// <summary>Onay kuralı eşleşmesi: kural adı ve sürümü, adım, adlı onaylayıcı ve eşleşme nedeni (tutar içermez).</summary>
public sealed record ContractApprovalMatchDto(Guid RuleId, long RuleVersion, string Name, int Step, Guid? ApproverId, string Reason);

/// <summary>Onay adımı: adlı onaylayıcılar (boşsa onay izni olan herkes), tamamlandı mı ve onaylayanlar.</summary>
public sealed record ContractApprovalStepDto(int Step, IReadOnlyList<Guid> Approvers, bool Completed, IReadOnlyList<Guid> ApprovedBy, bool IsCurrent);

/// <summary>Verilmiş karar. <c>OnBehalfOf</c> vekâlet dahil kararın adına sayıldığı kişilerdir. <c>Decision</c>: Approved, Rejected ya da Returned.</summary>
public sealed record ContractApprovalDecisionDto(
    Guid Id, int VersionNo, int StepNo, Guid ActorId, IReadOnlyList<Guid> OnBehalfOf, string Decision, string? Reason, DateTimeOffset At);

/// <summary>Güncel sürümün onay durumu; <c>CanDecide</c> geçerli kullanıcının şu anki adımda karar verip veremeyeceğidir (verememe nedeni <c>CannotDecideReason</c>).</summary>
public sealed record ContractApprovalDto(
    string Status,
    int VersionNo,
    IReadOnlyList<ContractApprovalMatchDto> Rules,
    string? Note,
    IReadOnlyList<ContractApprovalStepDto> Steps,
    IReadOnlyList<ContractApprovalDecisionDto> Decisions,
    int CurrentStep,
    bool CanDecide,
    bool ActingAsDelegate,
    string? CannotDecideReason,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DueAt,
    bool IsOverdue);

/// <summary>Onay iş listesi satırı.</summary>
public sealed record ContractApprovalInboxItemDto(
    Guid ContractId,
    string Number,
    string Title,
    string Type,
    int VersionNo,
    Guid? OwnerId,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    int CurrentStep,
    int StepCount,
    IReadOnlyList<Guid> WaitingFor,
    bool CanDecide,
    bool ActingAsDelegate,
    string? CannotDecideReason,
    IReadOnlyList<string> Rules);

public sealed record ContractApprovalRuleDto(
    Guid Id,
    long Version,
    string Name,
    string? Description,
    int Priority,
    int Step,
    bool IsActive,
    string? ContractType,
    string? Currency,
    decimal? MinGrossTotal,
    decimal? MinDiscountPercent,
    int? MinTermMonths,
    bool RequiresNonStandardTerms,
    string? MinRiskLevel,
    Guid? ApproverId);

public sealed record SaveContractApprovalRuleRequest(
    string Name,
    string? Description = null,
    int Priority = 0,
    int Step = 1,
    bool IsActive = true,
    string? ContractType = null,
    string? Currency = null,
    decimal? MinGrossTotal = null,
    decimal? MinDiscountPercent = null,
    int? MinTermMonths = null,
    bool RequiresNonStandardTerms = false,
    string? MinRiskLevel = null,
    Guid? ApproverId = null,
    long? Version = null);

public sealed record ContractApprovalDelegationDto(
    Guid Id, long Version, Guid FromUserId, Guid ToUserId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, bool IsActive, string? Note);

public sealed record SaveContractApprovalDelegationRequest(
    Guid FromUserId,
    Guid ToUserId,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    bool IsActive = true,
    string? Note = null,
    long? Version = null);

/// <summary>Onay kararı eylem anahtarları (API yolları); arayüz etiketleri bunlarla eşleşir.</summary>
public static class ContractActionNames
{
    public const string Approve = "approve";
    public const string Reject = "reject-approval";
    public const string Return = "return-for-changes";
}
