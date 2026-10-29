using System.Text.Json.Nodes;

namespace Modulix.Shared.Crm;

// Aktivite yaşam döngüsü, ilişki, katılımcı, takvim ve toplu işlem sözleşmeleri (ADR-0015). Eylemler kaydın sürümünü ister.

public sealed record ActivityVersionRequest(long Version);

public sealed record ActivityReasonRequest(long Version, string? Reason);

public sealed record ActivityNextStepRequest(string Subject, string? Type = null, DateTimeOffset? DueAt = null, Guid? OwnerId = null);

public sealed record CompleteActivityRequest(
    long Version, string? Outcome = null, string? Note = null, DateTimeOffset? CompletedAt = null, int? ActualMinutes = null,
    ActivityNextStepRequest? NextStep = null);

public sealed record CompleteActivityResultDto(ActivityDto Activity, ActivityDto? NextStep);

/// <summary>Yeni zaman bilgisinin tamamı. Çakışma varsa 400 ve <c>conflict</c> alan hatası döner; <c>AllowConflict</c> ile yine de planlanır.</summary>
public sealed record RescheduleActivityRequest(
    long Version, string? Reason, DateTimeOffset? DueAt = null, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null,
    bool IsAllDay = false, DateOnly? StartDate = null, DateOnly? EndDate = null, bool AllowConflict = false);

public sealed record ReassignActivityRequest(long Version, Guid OwnerId);

public sealed record ActivityLinkDto(
    Guid Id, long Version, Guid ActivityId, string EntityKey, Guid RecordId, bool IsPrimary, string? Title = null);

public sealed record SaveActivityLinkRequest(Guid ActivityId, string EntityKey, Guid RecordId, bool IsPrimary = false, long? Version = null);

public sealed record ActivityParticipantDto(
    Guid Id, long Version, Guid ActivityId, Guid? UserId, Guid? ContactId, string? Email, string? DisplayName, string Role);

public sealed record SaveActivityParticipantRequest(
    Guid ActivityId, Guid? UserId = null, Guid? ContactId = null, string? Email = null, string? DisplayName = null,
    string Role = "Required", long? Version = null);

/// <summary>Toplu işlem: her kayıt kendi yetki, kapsam ve sürüm denetiminden geçer; sonuç kayıt başınadır, kısmi başarı normaldir.</summary>
/// <param name="Action">complete | cancel | reopen | reassign | reschedule-by-days</param>
public sealed record BulkActivityRequest(
    string Action, IReadOnlyList<BulkActivityItem> Items, string? Reason = null, Guid? OwnerId = null, int? Days = null);

public sealed record BulkActivityItem(Guid Id, long Version);

public sealed record BulkActivityResultDto(IReadOnlyList<BulkActivityFailureDto> Failures, int Succeeded)
{
    public int Failed => Failures.Count;
}

public sealed record BulkActivityFailureDto(Guid Id, string Message);

/// <summary>Bir aktivitenin bağlı kayıtları. Başlık yalnızca çağıranın görebildiği kayıtlar için gelir; görünmeyen kayıt gizlenir.</summary>
public sealed record ActivityRelatedDto(IReadOnlyList<ActivityLinkDto> Links, int HiddenCount);
