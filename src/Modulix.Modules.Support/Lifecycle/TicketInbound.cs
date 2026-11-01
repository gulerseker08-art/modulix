using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record InboundResult(string Outcome, Guid? TicketId, Guid? MessageId, string? TicketNumber);

/// <summary>
/// Gelen kanal girişi (ADR-0024 dilim 4): e-posta, web formu, telefon kaydı gibi bir kanal adaptörünün normalize ettiği mesajı talebe bağlar. Bu bir <b>adaptör sözleşmesidir</b>;
/// gerçek IMAP/webhook alıcısı bu sözleşmeyi çağırır (bugün böyle bir alıcı kurulu değildir). Kurallar:
/// tekrar (aynı kanal + mesaj kimliği) hiçbir şey yazmaz; otomatik yanıt/bounce talep açmaz ve talebe eklenmez (yanıt döngüsünü keser);
/// eşleştirme önce In-Reply-To/References, sonra konudaki talep numarasıyla yapılır ve <b>gönderen talebin başvuran/alıcı adresiyle aynı olmalıdır</b> (numara tahminiyle başkasının talebine mesaj sokulamaz);
/// eşleşme yoksa yeni talep açılır; kapalı talebe gelen mesaj ilişkili yeni talep açar; gönderen başına saatlik sınır seli keser.
/// Giriş, çağıranın kendi izinleriyle çalışır (talep oluşturma + mesaj yazma) ve kapsamı dışına çıkmaz.
/// </summary>
public interface ITicketInbound
{
    Task<RecordResult<InboundResult>> IngestAsync(InboundMessage message, CancellationToken cancellationToken);
}

internal sealed class TicketInbound(
    IRecordPipeline pipeline, ITicketMessages messages, ICurrentAccess access, SupportReader reader, SupportStoreContext context) : ITicketInbound
{
    private const string Key = SupportModule.Entities.Ticket;
    public const int MaxPerSenderPerHour = 30;
    public const string Throttled = "Throttled";

    private static readonly string[] Channels = ["Email", "Web", "Phone", "Portal", "Api"];

    public async Task<RecordResult<InboundResult>> IngestAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        foreach (var permission in new[] { SupportModule.Permissions.TicketsCreate, SupportModule.Permissions.MessagesReply })
        {
            if (!await access.HasPermissionAsync(permission, cancellationToken))
            {
                return RecordResult<InboundResult>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
            }
        }

        if (Validate(message) is { } invalid)
        {
            return invalid;
        }

        var channel = Channels.First(c => c.Equals(message.Channel, StringComparison.OrdinalIgnoreCase));
        var externalId = message.ExternalMessageId.Trim();
        var sender = NormalizeAddress(message.FromAddress);

        // Tekrar: aynı kanalın aynı mesaj kimliği.
        if (await DuplicateAsync(channel, externalId, cancellationToken) is { } duplicate)
        {
            return RecordResult<InboundResult>.Ok(duplicate);
        }

        // Otomatik yanıt ve bounce: ne talep açar ne talebe eklenir (yanıt döngüsünü keser).
        if (InboundRules.IsAutomatic(message))
        {
            return RecordResult<InboundResult>.Ok(new InboundResult(InboundOutcome.IgnoredAutomatic, null, null, null));
        }

        // Sel koruması: aynı gönderenden son bir saatte çok mesaj geldiyse reddedilir.
        if (sender is not null)
        {
            var since = context.Time.GetUtcNow().AddHours(-1);
            if (await context.Data.Set<TicketMessage>().CountAsync(m => m.SenderAddress == sender && m.CreatedAt >= since, cancellationToken) >= MaxPerSenderPerHour)
            {
                return RecordResult<InboundResult>.Ok(new InboundResult(Throttled, null, null, null));
            }
        }

        var body = message.Body!.Trim();
        if (body.Length > TicketMessage.BodyMaxLength)
        {
            body = body[..(TicketMessage.BodyMaxLength - 20)] + "\n[... kısaltıldı]";
        }

        var info = new InboundInfo(channel, externalId, message.InReplyTo?.Trim(), sender);
        Guid? parentId = null;
        if (await MatchAsync(message, sender, cancellationToken) is { } match)
        {
            // Birleştirilmiş talebin yeni mesajı hedefe gider; kapalı/iptal talebe mesaj eklenmez, ilişkili yeni talep açılır.
            var target = match;
            if (target.Status == TicketStatus.Merged && target.MergedIntoId is { } mergedInto
                && await pipeline.GetAsync(new RecordRef(Key, mergedInto), cancellationToken) is { IsSuccess: true } hop)
            {
                target = new Match(mergedInto, TicketWorkflow.Text(hop.Value!, "status") ?? TicketStatus.New, null);
            }

            if (!TicketStatus.IsFinal(target.Status))
            {
                var appended = await messages.AddAsync(target.Id, new AddMessageInput(MessageKind.CustomerMessage, body, info), cancellationToken);
                return appended.IsSuccess
                    ? RecordResult<InboundResult>.Ok(new InboundResult(InboundOutcome.Appended, target.Id, appended.Value!.Id, null))
                    : await AfterConflictAsync(appended.Error!, channel, externalId, cancellationToken);
            }

            parentId = target.Id;
        }

        return await CreateAsync(message, channel, externalId, sender, body, info, parentId, cancellationToken);
    }

    private async Task<RecordResult<InboundResult>> CreateAsync(
        InboundMessage message, string channel, string externalId, string? sender, string body, InboundInfo info, Guid? parentId, CancellationToken cancellationToken)
    {
        var subject = InboundRules.CleanSubject(message.Subject);
        if (subject.Length == 0)
        {
            subject = "(Konusuz)";
        }

        if (subject.Length > SupportTicket.SubjectMaxLength)
        {
            subject = subject[..SupportTicket.SubjectMaxLength];
        }

        var name = string.IsNullOrWhiteSpace(message.FromName) ? sender?.Split('@')[0] ?? "Bilinmeyen gönderen" : message.FromName.Trim();
        if (name.Length > SupportTicket.NameMaxLength)
        {
            name = name[..SupportTicket.NameMaxLength];
        }

        var values = new JsonObject
        {
            ["subject"] = subject,
            ["channel"] = channel,
            ["reporterName"] = name,
            ["reporterEmail"] = sender,
            ["clientKey"] = ClientKey(channel, externalId),
        };
        if (parentId is { } parent)
        {
            values["parentTicketId"] = parent.ToString();
        }

        var created = await pipeline.CreateAsync(new RecordCommand(Key, null, values), cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error!.Code == RecordErrorCode.Conflict && await DuplicateAsync(channel, externalId, cancellationToken) is { } raced
                ? RecordResult<InboundResult>.Ok(raced)
                : RecordResult<InboundResult>.Fail(created.Error.Code, created.Error.Message, created.Error.FieldErrors);
        }

        var ticketId = created.Value!.Id;
        var added = await messages.AddAsync(ticketId, new AddMessageInput(MessageKind.CustomerMessage, body, info), cancellationToken);
        return added.IsSuccess
            ? RecordResult<InboundResult>.Ok(new InboundResult(InboundOutcome.Created, ticketId, added.Value!.Id, TicketWorkflow.Text(created.Value, "number")))
            : RecordResult<InboundResult>.Fail(added.Error!.Code, added.Error.Message, added.Error.FieldErrors);
    }

    private sealed record Match(Guid Id, string Status, Guid? MergedIntoId);

    /// <summary>Mesaj kimliği zincirine, yoksa konudaki talep numarasına göre talebi bulur; gönderen talebin adresiyle uyuşmalı ve talep çağıranın kapsamında olmalıdır.</summary>
    private async Task<Match?> MatchAsync(InboundMessage message, string? sender, CancellationToken cancellationToken)
    {
        if (sender is null)
        {
            return null;
        }

        var ids = InboundRules.ThreadIds(message);
        Guid? ticketId = null;
        if (ids.Count > 0)
        {
            var byThread = await context.Data.Set<TicketMessage>().AsNoTracking()
                .Where(m => m.ExternalMessageId != null && ids.Contains(m.ExternalMessageId)).OrderByDescending(m => m.CreatedAt)
                .Select(m => (Guid?)m.TicketId).FirstOrDefaultAsync(cancellationToken);
            ticketId = byThread;
        }

        if (ticketId is null && InboundRules.TicketNumberFromSubject(message.Subject) is { } number)
        {
            ticketId = await context.Data.Set<SupportTicket>().AsNoTracking().Where(t => t.Number == number).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken);
        }

        if (ticketId is not { } id)
        {
            return null;
        }

        // Kapsam (404 → eşleşme yok) ve gönderen doğrulaması.
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return null;
        }

        var ticket = await context.Data.Set<SupportTicket>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var (recipient, _) = await reader.RecipientAsync(ticket, cancellationToken);
        var known = new[] { NormalizeAddress(ticket.ReporterEmail), NormalizeAddress(recipient) };
        if (!known.Contains(sender))
        {
            return null;
        }

        return new Match(id, ticket.Status, ticket.MergedIntoId);
    }

    private async Task<InboundResult?> DuplicateAsync(string channel, string externalId, CancellationToken cancellationToken)
    {
        var existing = await context.Data.Set<TicketMessage>().AsNoTracking().Where(m => m.Channel == channel && m.ExternalMessageId == externalId)
            .Select(m => new { m.Id, m.TicketId }).FirstOrDefaultAsync(cancellationToken);
        return existing is null ? null : new InboundResult(InboundOutcome.Duplicate, existing.TicketId, existing.Id, null);
    }

    private async Task<RecordResult<InboundResult>> AfterConflictAsync(RecordError error, string channel, string externalId, CancellationToken cancellationToken) =>
        error.Code == RecordErrorCode.Conflict && await DuplicateAsync(channel, externalId, cancellationToken) is { } raced
            ? RecordResult<InboundResult>.Ok(raced)
            : RecordResult<InboundResult>.Fail(error.Code, error.Message, error.FieldErrors);

    private static RecordResult<InboundResult>? Validate(InboundMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Channel) || !Channels.Contains(m.Channel.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Invalid("channel", $"Şunlardan biri olmalıdır: {string.Join(", ", Channels)}.");
        }

        if (string.IsNullOrWhiteSpace(m.ExternalMessageId) || m.ExternalMessageId.Trim().Length > InboundRules.MaxMessageIdLength)
        {
            return Invalid("externalMessageId", $"Mesaj kimliği zorunludur (en fazla {InboundRules.MaxMessageIdLength} karakter).");
        }

        if (string.IsNullOrWhiteSpace(m.Body))
        {
            return Invalid("body", "Mesaj metni zorunludur.");
        }

        if (!string.IsNullOrWhiteSpace(m.FromAddress) && NormalizeAddress(m.FromAddress) is null)
        {
            return Invalid("fromAddress", "Gönderen adresi geçerli bir e-posta olmalıdır.");
        }

        if (m.Channel.Trim().Equals("Email", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(m.FromAddress))
        {
            return Invalid("fromAddress", "E-posta kanalında gönderen adresi zorunludur.");
        }

        return null;
    }

    /// <summary>Küçük harfe çevrilmiş, tek ve basit biçimli adres; geçersizse null.</summary>
    internal static string? NormalizeAddress(string? address)
    {
        var text = address?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length > SupportTicket.EmailMaxLength || text.Contains(' ') || text.Contains(','))
        {
            return null;
        }

        var at = text.IndexOf('@');
        return at > 0 && at == text.LastIndexOf('@') && at < text.Length - 3 && text.IndexOf('.', at) > at + 1 ? text : null;
    }

    private static string ClientKey(string channel, string externalId) =>
        "in:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{channel}|{externalId}")))[..40];

    private static RecordResult<InboundResult> Invalid(string field, string message) =>
        RecordResult<InboundResult>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
