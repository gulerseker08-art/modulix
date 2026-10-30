namespace Modulix.Shared.Crm;

// Teklif iç onay kuralları ve revizyon karşılaştırma sözleşmeleri (ADR-0019, dilim 4).

public sealed record QuoteApprovalRuleDto(
    Guid Id,
    long Version,
    string Name,
    string? Description,
    int Priority,
    bool IsActive,
    string? Currency,
    decimal? MinDiscountPercent,
    decimal? MinGrossTotal,
    Guid? ApproverId,
    int Step = 1);

/// <summary>
/// Kural: girilen tüm koşullar birlikte sağlanırsa (VE) onay gerekir; kurallar arasında biri yeter (VEYA). En az bir koşul zorunludur.
/// <see cref="ApproverId"/> doluysa onay yalnızca o kullanıcıdan kabul edilir.
/// </summary>
public sealed record SaveQuoteApprovalRuleRequest(
    string Name,
    string? Description = null,
    int Priority = 0,
    bool IsActive = true,
    string? Currency = null,
    decimal? MinDiscountPercent = null,
    decimal? MinGrossTotal = null,
    Guid? ApproverId = null,
    long? Version = null,
    int Step = 1);

public sealed record QuoteApprovalMatchDto(Guid RuleId, long RuleVersion, string Name, string Reason, Guid? ApproverId, int Step = 1);

/// <summary>Onay adımı: adlı onaylayanlar (boşsa onay izni olan herkes), tamamlanıp tamamlanmadığı, kimlerin onayladığı.</summary>
public sealed record QuoteApprovalStepDto(int Step, IReadOnlyList<Guid> Approvers, bool Completed, IReadOnlyList<Guid> ApprovedBy);

public sealed record QuoteApprovalDelegationDto(
    Guid Id, long Version, Guid FromUserId, Guid ToUserId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, bool IsActive, string? Note);

public sealed record SaveQuoteApprovalDelegationRequest(
    Guid? FromUserId, Guid? ToUserId, DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null, bool IsActive = true, string? Note = null, long? Version = null);

public sealed record QuoteApprovalRequirementDto(
    bool Required, IReadOnlyList<QuoteApprovalMatchDto> Matches, string? ThresholdReason, bool Restricted, bool CanDecide, string? Note,
    IReadOnlyList<QuoteApprovalStepDto> Steps, int CurrentStep, bool ActingAsDelegate);

public sealed record QuoteFieldChangeDto(string Field, string Label, string? Before, string? After);

/// <summary><c>Change</c>: Added, Removed, Changed ya da Unchanged.</summary>
public sealed record QuoteLineChangeDto(string Change, Guid StableKey, string Name, IReadOnlyList<QuoteFieldChangeDto> Fields);

public sealed record QuoteComparisonDto(
    int LeftRevision, int RightRevision, string LeftStatus, string RightStatus, string Currency, string RightCurrency,
    IReadOnlyList<QuoteFieldChangeDto> Header, IReadOnlyList<QuoteFieldChangeDto> Totals, IReadOnlyList<QuoteLineChangeDto> Lines);
