namespace Modulix.Shared.Platform;

public enum BillingStatusDto
{
    Pending = 1,
    Paid = 2,
    Cancelled = 3,
}

/// <summary>Firmanın bir dönem için ödeme kaydı (ADR-0028 Dilim 7c). <c>IsOverdue</c> bilgidir, erişimi değiştirmez.</summary>
public sealed record BillingRecordDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount,
    string Currency,
    DateOnly? DueDate,
    string? Reference,
    string? Note,
    BillingStatusDto Status,
    DateTimeOffset? PaidAt,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    bool IsOverdue);

/// <summary><c>Currency</c> boşsa TRY.</summary>
public sealed record CreateBillingRequest(
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal Amount, string? Currency = null, DateOnly? DueDate = null, string? Reference = null, string? Note = null);

public sealed record MarkBillingPaidRequest(DateTimeOffset? PaidAt = null, string? Reference = null);

public sealed record CancelBillingRequest(string Reason);
