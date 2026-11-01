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
    Guid Id, long Version, string Name, Guid? ParentId, Guid? DefaultQueueId, bool IsActive, int Position, Guid? CreatedBy, Guid? UpdatedBy);

public sealed record SaveCategoryRequest(
    long Version = 0, string? Name = null, Guid? ParentId = null, Guid? DefaultQueueId = null, bool IsActive = true, int Position = 0);

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
    Guid Id, string Visibility, string Direction, string Body, Guid? AuthorId, string DeliveryStatus, bool IsAutomatic, DateTimeOffset CreatedAt);

public sealed record TicketHistoryDto(
    Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, Guid? FromOwnerId, Guid? ToOwnerId,
    Guid? FromQueueId, Guid? ToQueueId, string? Reason);

public sealed record TicketSummaryDto(int Mine, int Unassigned, int AwaitingFirstResponse, int WaitingCustomer, int Reopened, int Resolved, int Open);

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

    public const string TicketEntityKey = "support.ticket";
    public const string QueueEntityKey = "support.queue";
    public const string CategoryEntityKey = "support.category";
}
