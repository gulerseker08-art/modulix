namespace Modulix.Shared.Orders;

// Operasyon işi API sözleşmeleri (ADR-0023, dilim 4). Durum, atama, gerçekleşen tarihler ve uyarı işaretleri sunucu tarafından yazılır; istekte gönderilmez.
// PUT tam durum gönderir (platform kuralı): gönderilmeyen düzenlenebilir alan temizlenir.

public static class OperationJobAccess
{
    public const string JobsAssign = "orders.jobs.assign";
    public const string JobsManage = "orders.jobs.manage";
}

public sealed record OperationJobDto(
    Guid Id,
    long Version,
    Guid OrderId,
    Guid? HandoverId,
    string Number,
    string Title,
    string Kind,
    string? GroupKey,
    string Status,
    string Priority,
    Guid? ResponsibleUserId,
    string? TeamName,
    DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualEndAt,
    string? Location,
    string? Notes,
    string? CancelReason,
    string Source,
    DateTimeOffset? UnassignedAlertAt,
    DateTimeOffset? OverdueAlertAt,
    Guid? CreatedBy);

public sealed record SaveOperationJobRequest(
    long Version,
    string? Title = null,
    string Priority = "Normal",
    DateOnly? PlannedStartDate = null,
    DateOnly? PlannedEndDate = null,
    string? Location = null,
    string? Notes = null);

public sealed record OperationTaskDto(Guid Id, long Version, int Position, string Title, bool IsRequired, bool IsDone, Guid? DoneBy, DateTimeOffset? DoneAt, string? Note, bool IsDefault);

/// <summary>Görev: oluşturmada <paramref name="JobId"/> ve <paramref name="Title"/>; güncellemede <paramref name="Version"/> ve tamamlandı/not.</summary>
public sealed record SaveOperationTaskRequest(Guid? JobId = null, string? Title = null, bool IsDone = false, string? Note = null, long? Version = null);

public sealed record OperationJobLineDto(Guid OrderLineId, string Name, string Kind, decimal Quantity, string? Unit);

public sealed record OperationJobLinkDto(Guid JobId, string Number, string Title, string Status);

public sealed record OperationJobEventDto(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Note);

public sealed record OperationJobActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

public sealed record OperationJobDetailDto(
    OperationJobDto Job, string OrderNumber, IReadOnlyList<OperationJobLineDto> Lines, IReadOnlyList<OperationTaskDto> Tasks, IReadOnlyList<OperationJobLinkDto> DependsOn,
    IReadOnlyList<OperationJobLinkDto> Dependents, IReadOnlyList<OperationJobActionDto> Actions, IReadOnlyList<OperationJobEventDto> History, int ProgressPercent);

/// <summary>Elle iş açma: bağlanacak sipariş kalemleri zorunlu; sorumlu/ekip verilmezse iş "Atama bekliyor" durumunda açılır.</summary>
public sealed record CreateOperationJobRequest(
    IReadOnlyList<Guid> LineIds, string? Title = null, string? Priority = null, Guid? ResponsibleUserId = null, string? TeamName = null,
    DateOnly? PlannedStartDate = null, DateOnly? PlannedEndDate = null, string? Location = null, string? Notes = null);

/// <summary>İş eylemi: <c>assign</c> için sorumlu kullanıcı ve/veya ekip; <c>cancel</c> için gerekçe. Kayıt sürümü zorunlu.</summary>
public sealed record OperationJobActionRequest(long Version, string? Reason = null, Guid? ResponsibleUserId = null, string? TeamName = null);

public sealed record AddJobDependencyRequest(Guid DependsOnJobId);
