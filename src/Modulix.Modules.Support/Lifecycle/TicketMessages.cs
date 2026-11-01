using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>Mesaj türleri; adlar API değeridir.</summary>
public static class MessageKind
{
    /// <summary>Müşteriye görünen yanıt (destek ekibinden müşteriye).</summary>
    public const string Reply = "reply";

    /// <summary>Müşteriden gelen mesajın kaydı (telefon, e-posta, portal).</summary>
    public const string CustomerMessage = "customer-message";

    /// <summary>İç not: müşteriye hiçbir yoldan gitmez.</summary>
    public const string Note = "note";

    /// <summary>Müşterinin çözümü onayladığının kaydı: çözülmüş talebi hemen kapatır (zaman aşımı beklenmez).</summary>
    public const string CustomerConfirmation = "customer-confirmation";

    public static readonly IReadOnlyList<string> All = [Reply, CustomerMessage, Note, CustomerConfirmation];
}

public sealed record AddMessageInput(string Kind, string? Body);

/// <summary>Mesaj (API görünümü). İç not yalnızca iç not okuma izni olana verilir.</summary>
public sealed record TicketMessageItem(
    Guid Id, string Visibility, string Direction, string Body, Guid? AuthorId, string DeliveryStatus, bool IsAutomatic, DateTimeOffset CreatedAt, Guid? SourceTicketId = null);

/// <summary>
/// Talep mesajları (ADR-0024). Müşteriye görünen yanıt ile iç not kesin ayrıdır: ayrım veritabanında da bir kısıtla korunur ve iç not yalnızca
/// bu servis üzerinden, iç not okuma izni olana döner (genel kayıt uçlarında mesaj yoktur). Mesaj, talebin güncellemesiyle aynı işlemde
/// yazılır (son hareket, ilk yanıt, müşteri mesajının durum etkisi). Mesajlar değiştirilmez ve silinmez.
/// </summary>
public interface ITicketMessages
{
    Task<RecordResult<IReadOnlyList<TicketMessageItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<TicketMessageItem>> AddAsync(Guid ticketId, AddMessageInput input, CancellationToken cancellationToken);
}

internal sealed class TicketMessages(
    IRecordPipeline pipeline,
    TicketWorkflowGate gate,
    ICurrentAccess access,
    SupportReader reader,
    IEmailQueue emailQueue) : ITicketMessages
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<TicketMessageItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<TicketMessageItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var includeInternal = (await access.GetPermissionsAsync(cancellationToken)).Contains(SupportModule.Permissions.InternalRead);
        var messages = await reader.MessagesAsync(ticketId, includeInternal, cancellationToken);
        return RecordResult<IReadOnlyList<TicketMessageItem>>.Ok([.. messages.Select(ToItem)]);
    }

    public async Task<RecordResult<TicketMessageItem>> AddAsync(Guid ticketId, AddMessageInput input, CancellationToken cancellationToken)
    {
        if (!MessageKind.All.Contains(input.Kind))
        {
            return Invalid("kind", $"Şunlardan biri olmalıdır: {string.Join(", ", MessageKind.All)}.");
        }

        var (visibility, direction, permission, eventKind) = input.Kind switch
        {
            MessageKind.Reply => (MessageVisibility.Public, MessageDirection.Outbound, SupportModule.Permissions.MessagesReply, TicketEventKinds.PublicReply),
            MessageKind.CustomerMessage => (MessageVisibility.Public, MessageDirection.Inbound, SupportModule.Permissions.MessagesReply, TicketEventKinds.CustomerMessage),
            MessageKind.CustomerConfirmation => (MessageVisibility.Public, MessageDirection.Inbound, SupportModule.Permissions.MessagesReply, TicketEventKinds.CustomerConfirmed),
            _ => (MessageVisibility.Internal, MessageDirection.Note, SupportModule.Permissions.NotesWrite, TicketEventKinds.InternalNote),
        };

        // Önce kapsamla okunur (kapsam dışı talep 404), sonra izin denetlenir (izinsiz mesaj 403).
        var loaded = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<TicketMessageItem>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<TicketMessageItem>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var body = input.Body?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            return Invalid("body", "Mesaj metni zorunludur.");
        }

        if (body.Length > TicketMessage.BodyMaxLength)
        {
            return Invalid("body", $"En fazla {TicketMessage.BodyMaxLength} karakter olabilir.");
        }

        var status = TicketWorkflow.Text(loaded.Value!, "status") ?? TicketStatus.New;
        if (TicketStatus.IsFinal(status))
        {
            return RecordResult<TicketMessageItem>.Fail(RecordErrorCode.Conflict,
                "Kapanmış ya da iptal edilmiş talebe mesaj eklenemez; yeniden açın (pencere içindeyse) ya da ilişkili yeni talep açın.",
                [new RecordFieldError("status", "Talep kapalı.")]);
        }

        var values = new JsonObject { ["lastActivityAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        if (input.Kind == MessageKind.CustomerMessage)
        {
            // Müşteri yanıtı bekleyen talep işleme döner; çözülmüş talebe gelen müşteri mesajı (itiraz) talebi yeniden açar.
            if (status == TicketStatus.WaitingCustomer)
            {
                values["status"] = TicketStatus.InProgress;
                values["statusReason"] = null;
            }
            else if (status == TicketStatus.Resolved)
            {
                values["status"] = TicketStatus.Reopened;
                values["statusReason"] = "Müşteri çözüme itiraz etti / yeni mesaj gönderdi.";
                values["closedAt"] = null;
                values["reopenCount"] = (loaded.Value!.Values["reopenCount"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0) + 1;
            }
        }

        if (input.Kind == MessageKind.CustomerConfirmation)
        {
            if (status != TicketStatus.Resolved)
            {
                return RecordResult<TicketMessageItem>.Fail(RecordErrorCode.Conflict, "Müşteri teyidi yalnızca çözülmüş talepte kaydedilir.", [new RecordFieldError("status", "Talep çözülmüş değil.")]);
            }

            // Teyit çözülmüş talebi kapatır; çözüm kaydı ve geçmiş korunur.
            values["status"] = TicketStatus.Closed;
            values["closedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        TicketMessage? created = null;
        Func<SupportStoreContext, SupportTicket, CancellationToken, Task> effect = async (context, ticket, ct) =>
        {
            var delivery = DeliveryStatus.NotRequired;
            if (input.Kind == MessageKind.Reply)
            {
                delivery = await DeliverAsync(ticket, body, ct);
            }
            else if (input.Kind is MessageKind.CustomerMessage or MessageKind.CustomerConfirmation)
            {
                delivery = DeliveryStatus.Recorded;
            }

            created = new TicketMessage
            {
                TicketId = ticket.Id,
                Visibility = visibility,
                Direction = direction,
                Body = body,
                AuthorId = context.CurrentUserId,
                DeliveryStatus = delivery,
                IsAutomatic = false,
            };
            context.Data.Set<TicketMessage>().Add(created);

            // İlk insan yanıtı: otomatik olmayan, müşteriye giden ve gönderimi kabul edilmiş ilk mesaj. İç not ve müşteri mesajı sayılmaz.
            if (ticket.FirstResponseAt is null && FirstResponseRules.Counts(visibility, direction, false, delivery))
            {
                ticket.FirstResponseAt = DateTimeOffset.UtcNow;
            }
        };

        using (gate.Open([permission], eventKind, null, effect))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, null) { Reason = $"support.{eventKind}" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<TicketMessageItem>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        return RecordResult<TicketMessageItem>.Ok(ToItem(created!));
    }

    /// <summary>
    /// Yanıtı alıcıya e-postayla kuyruğa alır. Kuyruğa alındı = gönderim kabul edildi; müşteriye ulaştı demek değildir (teslim/başarısızlık
    /// kanal adaptörünün işidir, dilim 4). Alıcı adresi yoksa yanıt yalnızca kaydedilir (telefon, portal vb.); geçersiz adres başarısızdır.
    /// </summary>
    private async Task<string> DeliverAsync(SupportTicket ticket, string body, CancellationToken cancellationToken)
    {
        var (address, name) = await reader.RecipientAsync(ticket, cancellationToken);
        if (address is null)
        {
            return DeliveryStatus.Recorded;
        }

        try
        {
            var encoded = WebUtility.HtmlEncode(body).Replace("\r\n", "\n").Replace("\n", "<br />");
            emailQueue.Enqueue(new EmailMessage(address, $"[{ticket.Number}] {ticket.Subject}", $"<p>{encoded}</p><p>Talep no: {WebUtility.HtmlEncode(ticket.Number)}</p>")
            {
                ToName = name,
                TextBody = $"{body}\n\nTalep no: {ticket.Number}",
                Reference = $"support.reply:{ticket.Id}",
            });
            return DeliveryStatus.Queued;
        }
        catch (ArgumentException)
        {
            return DeliveryStatus.Failed;
        }
    }

    private static TicketMessageItem ToItem(TicketMessage m) =>
        new(m.Id, m.Visibility, m.Direction, m.Body, m.AuthorId, m.DeliveryStatus, m.IsAutomatic, m.CreatedAt, m.SourceTicketId);

    private static RecordResult<TicketMessageItem> Invalid(string field, string message) =>
        RecordResult<TicketMessageItem>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
