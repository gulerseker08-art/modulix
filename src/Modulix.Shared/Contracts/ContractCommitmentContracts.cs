namespace Modulix.Shared.Contracts;

// ---- Dilim 7: taahhüt ve yükümlülükler

/// <summary>Taahhüt tanımı. <c>Status</c> sistem alanıdır (Active, Completed, Cancelled). Ceza ya da finansal sonuç uygulanmaz; <c>ConsequenceNote</c> yalnızca sözleşme maddesine atıftır.</summary>
public sealed record ContractCommitmentDto(
    Guid Id,
    long Version,
    Guid ContractId,
    string Title,
    string? Description,
    string Category,
    string Obligor,
    Guid? ResponsibleUserId,
    Guid? EscalationUserId,
    string Kind,
    DateOnly StartDate,
    DateOnly DueDate,
    string? Recurrence,
    DateOnly? RecurrenceEndDate,
    int GraceDays,
    int EscalateAfterDays,
    string? MetricName,
    string? Unit,
    string? Direction,
    decimal? TargetValue,
    decimal? Tolerance,
    Guid? DependsOnCommitmentId,
    string? ConsequenceNote,
    string Status,
    Guid? CreatedBy,
    System.Text.Json.Nodes.JsonObject? Custom = null);

/// <summary>
/// Taahhüt kaydı. <c>Obligor</c>: Provider, Customer, Both. <c>Kind</c>: OneTime ya da Periodic (tekrar kuralı Weekly, Monthly, Quarterly, Yearly). Ölçüt adı girildiyse
/// <c>Direction</c> (AtLeast, AtMost, Exactly) ve <c>TargetValue</c> zorunludur. Gerçekleşmesi kaydedilmiş taahhütte takvim ve ölçüm değiştirilemez.
/// </summary>
public sealed record SaveContractCommitmentRequest(
    Guid ContractId,
    string Title,
    DateOnly StartDate,
    DateOnly DueDate,
    string? Description = null,
    string? Category = null,
    string Obligor = "Provider",
    Guid? ResponsibleUserId = null,
    Guid? EscalationUserId = null,
    string Kind = "OneTime",
    string? Recurrence = null,
    DateOnly? RecurrenceEndDate = null,
    int GraceDays = 0,
    int EscalateAfterDays = 0,
    string? MetricName = null,
    string? Unit = null,
    string? Direction = null,
    decimal? TargetValue = null,
    decimal? Tolerance = null,
    Guid? DependsOnCommitmentId = null,
    string? ConsequenceNote = null,
    long? Version = null,
    System.Text.Json.Nodes.JsonObject? Custom = null);

/// <summary>Dönem gerçekleşmesi. Kanıt dosyası (PDF/PNG/JPEG, en fazla 10 MB) base64 gelir; ölçümlü taahhütte <c>ActualValue</c> zorunludur.</summary>
public sealed record CompleteOccurrenceRequest(DateOnly CompletedOn, decimal? ActualValue = null, string? Note = null, string? FileName = null, string? ContentBase64 = null);

public sealed record CommitmentEvaluateRequest(Guid? ContractId = null);

public sealed record CommitmentEvaluationDto(int Breached, int Escalated, int FollowUpsCreated, int FollowUpsUnavailable, int OccurrencesCreated);

/// <summary>Gerçekleşme. <c>Status</c>: Open, Fulfilled, Breached, Waived, Cancelled; <c>IsOverdue</c> türetilir (son tarihi geçmiş, tamamlanmamış). <c>BlockedByDue</c> doluysa bağımlı taahhüt engeli vardır.</summary>
public sealed record CommitmentOccurrenceDto(
    Guid Id,
    Guid CommitmentId,
    Guid ContractId,
    string ContractNumber,
    string ContractTitle,
    string CommitmentTitle,
    string Category,
    Guid? ResponsibleUserId,
    int Sequence,
    DateOnly DueDate,
    string Status,
    bool IsOverdue,
    int DaysOverdue,
    DateOnly? CompletedOn,
    Guid? CompletedBy,
    DateTimeOffset? RecordedAt,
    decimal? ActualValue,
    bool? MetTarget,
    string? MetricName,
    string? Unit,
    string? Direction,
    decimal? TargetValue,
    decimal? Tolerance,
    string? Note,
    Guid? EvidenceFileId,
    string? EvidenceFileName,
    DateTimeOffset? BreachedAt,
    string? BreachReason,
    string FollowUpStatus,
    Guid? FollowUpActivityId,
    DateTimeOffset? EscalatedAt,
    Guid? EscalationActivityId,
    Guid? WaivedBy,
    string? WaiveReason,
    DateOnly? BlockedByDue,
    bool CanComplete);
