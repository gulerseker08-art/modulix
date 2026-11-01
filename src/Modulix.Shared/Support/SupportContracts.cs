namespace Modulix.Shared.Support;

// Destek modülü API sözleşmeleri (ADR-0024). Durum, sorumlu, kuyruk, öncelik, çözüm ve zaman damgaları sunucu tarafından yazılır; genel
// kayıt isteğinde gönderilse bile yok sayılır. Bu alanlar yalnızca eylem uç noktalarıyla (atama, geçiş, çözüm) değişir.

public sealed record TicketDto(
    Guid Id,
    long Version,
    string Number,
    string Subject,
    string? Description,
    string TypeKey,
    string Channel,
    Guid? PartyId,
    Guid? PersonId,
    string? CustomerName,
    string? ReporterName,
    string? ReporterEmail,
    string? ReporterPhone,
    Guid? CategoryId,
    string Impact,
    string Urgency,
    string Priority,
    bool PriorityOverridden,
    string? PriorityReason,
    string Status,
    string? StatusReason,
    Guid? QueueId,
    Guid? OwnerId,
    Guid? ParentTicketId,
    Guid? MergedIntoId,
    string? ClientKey,
    DateOnly? EventDate,
    string? ExpectedOutcome,
    string? ResolutionCode,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    string? RootCause,
    string? CorrectiveAction,
    DateTimeOffset? ClosedAt,
    int ReopenCount,
    DateTimeOffset? FirstResponseAt,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    Guid? UpdatedBy,
    System.Text.Json.Nodes.JsonObject? Custom = null);

/// <summary>
/// Talep oluşturma / düzenleme isteği. Müşteri seçilmediyse başvuran adı verilir (gereksiz müşteri kaydı açılmaz). Sorumlu ve kuyruk yalnızca
/// oluştururken ve yalnızca atama yetkisiyle verilebilir; sonradan atama eylemleriyle değişir. <paramref name="ClientKey"/>: tekrar koruması;
/// aynı anahtarla ikinci oluşturma isteği 409 alır ve yeni talep açmaz.
/// </summary>
public sealed record SaveTicketRequest(
    long Version = 0,
    string? Subject = null,
    string? Description = null,
    string? TypeKey = null,
    string? Channel = null,
    Guid? PartyId = null,
    Guid? PersonId = null,
    string? ReporterName = null,
    string? ReporterEmail = null,
    string? ReporterPhone = null,
    Guid? CategoryId = null,
    string? Impact = null,
    string? Urgency = null,
    Guid? OwnerId = null,
    Guid? QueueId = null,
    Guid? ParentTicketId = null,
    string? ClientKey = null,
    DateOnly? EventDate = null,
    string? ExpectedOutcome = null,
    System.Text.Json.Nodes.JsonObject? Custom = null);

public sealed record QueueDto(Guid Id, long Version, string Name, string? Description, bool IsDefault, bool IsActive, Guid? CreatedBy, Guid? UpdatedBy);

public sealed record SaveQueueRequest(long Version = 0, string? Name = null, string? Description = null, bool IsDefault = false, bool IsActive = true);

public sealed record CategoryDto(
    Guid Id, long Version, string Name, Guid? ParentId, Guid? DefaultQueueId, bool IsActive, int Position, string? RequiredFields, Guid? CreatedBy, Guid? UpdatedBy);

public sealed record SaveCategoryRequest(
    long Version = 0, string? Name = null, Guid? ParentId = null, Guid? DefaultQueueId = null, bool IsActive = true, int Position = 0, string? RequiredFields = null);

/// <summary>Eylem isteği: kaydın sürümü zorunlu (eşzamanlı çakışmada 409); gerekçe ve diğer alanlar eylemin türüne göre zorunludur.</summary>
public sealed record TicketActionRequest(
    long Version,
    string? Reason = null,
    Guid? OwnerId = null,
    Guid? QueueId = null,
    string? ResolutionCode = null,
    string? ResolutionNote = null,
    string? RootCause = null,
    string? CorrectiveAction = null,
    string? Priority = null);

public sealed record TicketActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <param name="Kind">reply (müşteriye görünen yanıt), customer-message (müşteriden gelen mesajın kaydı) ya da note (iç not).</param>
public sealed record AddTicketMessageRequest(string Kind, string? Body);

public sealed record TicketMessageDto(
    Guid Id, string Visibility, string Direction, string Body, Guid? AuthorId, string DeliveryStatus, bool IsAutomatic, DateTimeOffset CreatedAt, Guid? SourceTicketId = null);

public sealed record TicketHistoryDto(
    Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, Guid? FromOwnerId, Guid? ToOwnerId,
    Guid? FromQueueId, Guid? ToQueueId, string? Reason);

public sealed record TicketSummaryDto(int Mine, int Unassigned, int AwaitingFirstResponse, int WaitingCustomer, int Reopened, int Resolved, int Open, int SlaAtRisk = 0, int SlaBreached = 0);

/// <summary>Arayüzün izin ve modül anahtarları (sunucudaki <c>SupportModule</c> ile aynıdır).</summary>
public static class SupportAccess
{
    public const string ModuleKey = "support";
    public const string TicketsRead = "support.tickets.read";
    public const string TicketsCreate = "support.tickets.create";
    public const string TicketsWrite = "support.tickets.write";
    public const string TicketsAssign = "support.tickets.assign";
    public const string TicketsTake = "support.tickets.take";
    public const string TicketsResolve = "support.tickets.resolve";
    public const string TicketsClose = "support.tickets.close";
    public const string TicketsReopen = "support.tickets.reopen";
    public const string TicketsCancel = "support.tickets.cancel";
    public const string PriorityOverride = "support.tickets.priority";
    public const string MessagesReply = "support.messages.reply";
    public const string NotesWrite = "support.notes.write";
    public const string InternalRead = "support.internal.read";
    public const string SettingsManage = "support.settings.manage";
    public const string TicketsMerge = "support.tickets.merge";
    public const string SlaManage = "support.sla.manage";
    public const string SlaRecalculate = "support.sla.recalculate";

    public const string TicketEntityKey = "support.ticket";
    public const string QueueEntityKey = "support.queue";
    public const string CategoryEntityKey = "support.category";
    public const string SlaCalendarEntityKey = "support.sla-calendar";
    public const string SlaPolicyEntityKey = "support.sla-policy";
    public const string PriorityMatrixEntityKey = "support.priority-matrix";
    public const string RoutingRuleEntityKey = "support.routing-rule";
}

// ---- Dilim 2: SLA, iş takvimi ve eskalasyon (ADR-0024). Hesap backend'dedir; arayüz sayacı yalnızca gösterimdir.

public sealed record SlaCalendarDto(
    Guid Id, long Version, string Name, string TimeZoneId, bool IsAlwaysOpen, string? WeeklyHours, string? Holidays, bool IsActive, Guid? CreatedBy, Guid? UpdatedBy);

/// <param name="WeeklyHours">JSON: <c>[{"day":1,"start":"09:00","end":"18:00"}]</c> (0=Pazar … 6=Cumartesi).</param>
/// <param name="Holidays">JSON: <c>[{"date":"2026-10-29","name":"…","start":null,"end":null}]</c>; start/end verilirse yarım gün.</param>
public sealed record SaveSlaCalendarRequest(
    long Version = 0, string? Name = null, string? TimeZoneId = "Europe/Istanbul", bool IsAlwaysOpen = false, string? WeeklyHours = null, string? Holidays = null, bool IsActive = true);

public sealed record SlaPolicyDto(
    Guid Id, long Version, string Name, string? Description, bool IsActive, bool IsDefault, int Precedence, string? TypeKey, Guid? CategoryId, string? Priority, Guid? PartyId,
    int? FirstResponseMinutes, int? ResolutionMinutes, Guid? CalendarId, bool PauseOnWaitingCustomer, bool PauseOnWaitingInternal, string? EscalationRules, int Revision,
    Guid? CreatedBy, Guid? UpdatedBy);

/// <param name="EscalationRules">JSON: <c>[{"id":"warn","metric":"Resolution","percent":75,"action":"NotifyOwner","userId":null,"queueId":null}]</c>.</param>
public sealed record SaveSlaPolicyRequest(
    long Version = 0, string? Name = null, string? Description = null, bool IsActive = true, bool IsDefault = false, int Precedence = 100, string? TypeKey = null,
    Guid? CategoryId = null, string? Priority = null, Guid? PartyId = null, int? FirstResponseMinutes = null, int? ResolutionMinutes = null, Guid? CalendarId = null,
    bool PauseOnWaitingCustomer = true, bool PauseOnWaitingInternal = false, string? EscalationRules = null);

public sealed record SlaPauseDto(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason);

public sealed record SlaEscalationDto(string RuleId, int Percent, string Action, DateTimeOffset ThresholdAt, DateTimeOffset ExecutedAt, string? Outcome);

/// <param name="State">Running | Paused | Breached | Met | Missed | Cancelled.</param>
public sealed record SlaInstanceDto(
    Guid Id, int Cycle, string Metric, string Status, string State, string PolicyName, int PolicyRevision, DateTimeOffset StartedAt, int TargetMinutes, DateTimeOffset? DueAt,
    DateTimeOffset? WarningAt, int WorkedMinutes, int RemainingMinutes, DateTimeOffset? CompletedAt, DateTimeOffset? BreachedAt, string? CalendarName, bool AlwaysOpen,
    DateTimeOffset ComputedAt, IReadOnlyList<SlaPauseDto> Pauses, IReadOnlyList<SlaEscalationDto> Escalations);

public sealed record SlaRecalculateRequest(long Version, string? Reason = null);

public sealed record SlaPreviewRequest(string TypeKey, string Priority, Guid? CategoryId = null, Guid? PartyId = null, DateTimeOffset? StartAt = null);

public sealed record SlaPreviewMetricDto(string Metric, int TargetMinutes, DateTimeOffset DueAt, DateTimeOffset? WarningAt);

public sealed record SlaRuleDto(string Id, string Metric, int Percent, string Action, Guid? UserId, Guid? QueueId);

public sealed record SlaPreviewDto(
    bool Matched, Guid? PolicyId, string? PolicyName, int PolicyRevision, string? CalendarName, bool AlwaysOpen, bool PauseOnWaitingCustomer, bool PauseOnWaitingInternal,
    IReadOnlyList<SlaPreviewMetricDto> Metrics, IReadOnlyList<SlaRuleDto> Rules);

// ---- Dilim 3: yönetilebilir öncelik matrisi, yönlendirme kuralları, kategori alan kuralları, mükerrer, birleştirme ve alt işler (ADR-0024)

public sealed record PriorityCellDto(string Impact, string Urgency, string Priority);

/// <summary>Matris hücreleri (eksik hücre yerleşik değerle tamamlanır). Matris yalnızca sonradan açılan ya da etki/aciliyeti düzenlenen talepleri etkiler.</summary>
public sealed record SavePriorityMatrixRequest(IReadOnlyList<PriorityCellDto> Cells);

public sealed record RoutingRuleDto(
    Guid Id, long Version, string Name, int Order, bool IsActive, string? Channel, string? TypeKey, Guid? CategoryId, string? Priority, Guid? PartyId, Guid? QueueId, Guid? OwnerId,
    Guid? CreatedBy, Guid? UpdatedBy);

public sealed record SaveRoutingRuleRequest(
    long Version = 0, string? Name = null, int Order = 100, bool IsActive = true, string? Channel = null, string? TypeKey = null, Guid? CategoryId = null, string? Priority = null,
    Guid? PartyId = null, Guid? QueueId = null, Guid? OwnerId = null);

public sealed record RoutingPreviewRequest(string Channel, string TypeKey, string Priority, Guid? CategoryId = null, Guid? PartyId = null);

public sealed record RoutingPreviewDto(bool RuleMatched, Guid? RuleId, string? RuleName, int? RuleOrder, Guid? QueueId, Guid? OwnerId, string Source);

public sealed record TicketDuplicateDto(Guid Id, string Number, string Subject, string Status, DateTimeOffset CreatedAt, double Score, string Reason);

/// <param name="TargetId">Talebin birleştirileceği hedef talep. Kaynak talep (yoldaki) son durum <c>Merged</c> olur.</param>
public sealed record MergeTicketRequest(Guid TargetId, long Version, string? Reason = null);

public sealed record TicketTaskDto(Guid Id, string Title, Guid? AssigneeId, bool IsBlocking, bool IsDone, DateTimeOffset? DoneAt, Guid? DoneBy, Guid? CreatedBy, DateTimeOffset CreatedAt);

public sealed record AddTicketTaskRequest(string? Title, Guid? AssigneeId = null, bool IsBlocking = false);
