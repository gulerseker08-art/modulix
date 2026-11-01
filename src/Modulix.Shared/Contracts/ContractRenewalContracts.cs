namespace Modulix.Shared.Contracts;

// ---- Dilim 8: yenileme, fesih ve zamanlanmış işler

public sealed record RenewalDecisionDto(Guid Id, DateOnly PeriodEnd, string Outcome, DateOnly? NewEndDate, Guid? ResultContractId, string? Reason, string? Note, string Source, Guid? DecidedBy, DateTimeOffset At);

public sealed record ReminderDto(Guid Id, DateOnly PeriodKey, string Kind, int ThresholdDays, string Status, Guid? ActivityId, string? Note, DateTimeOffset At);

/// <summary>Sözleşmenin dönem, yenileme ve fesih durumu. <c>AutoRenewalBlockedReason</c> doluysa sözleşme kendiliğinden uzatılmaz (nedeni).</summary>
public sealed record RenewalOverviewDto(
    Guid ContractId,
    string EffectiveStatus,
    string RenewalMode,
    int? RenewalPeriodMonths,
    int? NoticePeriodDays,
    DateOnly? EndDate,
    DateOnly? NoticeDeadline,
    bool IsIndefinite,
    int? DaysToNotice,
    int? DaysToEnd,
    DateOnly? TerminationDate,
    string? TerminationReason,
    bool AutoRenewalAllowedByTenant,
    string? AutoRenewalBlockedReason,
    IReadOnlyList<int> Thresholds,
    RenewalDecisionDto? CurrentDecision,
    IReadOnlyList<RenewalDecisionDto> History,
    IReadOnlyList<ReminderDto> Reminders);

/// <summary>Yenileme taslağı: yeni dönem başlangıcı (boşsa mevcut bitişin ertesi günü) ve süre (boşsa yenileme süresi). Aynı dönem için tekrar çağrı aynı taslağı döndürür.</summary>
public sealed record RenewalDraftRequest(DateOnly? StartDate = null, int? TermMonths = null);

public sealed record RenewalDraftResultDto(Guid ContractId, string Number, bool AlreadyExisted);

/// <summary>Yenilememe kararı. <c>Reason</c>: Price, Competitor, NoLongerNeeded, ServiceQuality, CustomerClosed, Other.</summary>
public sealed record DeclineRenewalRequest(string Reason, string? Note = null);

/// <summary>Fesih: tarih (bugün ya da geçmişse hemen, gelecekse planlanır) ve gerekçe; bildirim süresinden kısa fesih için <c>ConfirmShortNotice</c> gerekir.</summary>
public sealed record TerminateContractRequest(DateOnly EffectiveDate, string Reason, bool ConfirmShortNotice = false);

public sealed record RenewalCalendarItemDto(
    Guid ContractId, string Number, string Title, Guid PartyId, Guid? OwnerId, DateOnly? EndDate, DateOnly? NoticeDeadline, int? DaysToNotice, int? DaysToEnd, string RenewalMode,
    string EffectiveStatus, string? Decision, Guid? ResultContractId, DateOnly? TerminationDate, string? LastReminder);

public sealed record ChainItemDto(Guid Id, string Number, string Title, DateOnly? StartDate, DateOnly? EndDate, string EffectiveStatus, string SignatureStatus, string Relation);

public sealed record SchedulerRunDto(
    int Activated, int PeriodsExpired, int Terminated, int AutoRenewed, int RemindersCreated, int RemindersSkipped, int EscalationsCreated, int SignaturesExpired, int ApprovalReminders,
    int CommitmentsBreached);
