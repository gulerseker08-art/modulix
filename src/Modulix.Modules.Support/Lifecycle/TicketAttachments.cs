using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record TicketAttachmentItem(
    Guid Id, Guid MessageId, string FileName, string ContentType, int SizeBytes, string Sha256, string ScanStatus, bool IsInternal, Guid? CreatedBy, DateTimeOffset CreatedAt);

public sealed record TicketAttachmentFile(TicketAttachmentItem Info, byte[] Content);

/// <summary>
/// Talep mesajlarının ekleri (ADR-0024 dilim 4). Ek bir mesaja bağlıdır ve görünürlüğünü ondan alır: iç nota bağlı ek iç not izni olmayana ne listede
/// görünür ne indirilir (404). Yazma, mesajı yazma izniyle (yanıt ya da iç not) ve talebin görünürlüğü içinde yapılır; ek talebin güncellemesiyle aynı
/// işlemde yazılır (tarihçe, sürüm). Tür uzantı + dosya imzasıyla doğrulanır; içerik taraması yoktur. Ekler değiştirilmez ve silinmez.
/// </summary>
public interface ITicketAttachments
{
    Task<RecordResult<IReadOnlyList<TicketAttachmentItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<TicketAttachmentItem>> AddAsync(Guid ticketId, Guid messageId, string? fileName, byte[] content, CancellationToken cancellationToken);

    Task<RecordResult<TicketAttachmentFile>> GetAsync(Guid ticketId, Guid attachmentId, CancellationToken cancellationToken);
}

internal sealed class TicketAttachments(IRecordPipeline pipeline, TicketWorkflowGate gate, ICurrentAccess access, SupportStoreContext context) : ITicketAttachments
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<TicketAttachmentItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<TicketAttachmentItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var includeInternal = await access.HasPermissionAsync(SupportModule.Permissions.InternalRead, cancellationToken);
        return RecordResult<IReadOnlyList<TicketAttachmentItem>>.Ok(await QueryAsync(ticketId, includeInternal, cancellationToken));
    }

    public async Task<RecordResult<TicketAttachmentItem>> AddAsync(Guid ticketId, Guid messageId, string? fileName, byte[] content, CancellationToken cancellationToken)
    {
        // Önce kapsamla okunur (kapsam dışı 404), sonra izin (403), sonra içerik.
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<TicketAttachmentItem>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var message = await context.Data.Set<TicketMessage>().AsNoTracking()
            .Where(m => m.Id == messageId && m.TicketId == ticketId).Select(m => new { m.Visibility }).FirstOrDefaultAsync(cancellationToken);
        var isInternal = message?.Visibility == MessageVisibility.Internal;
        var permission = isInternal ? SupportModule.Permissions.NotesWrite : SupportModule.Permissions.MessagesReply;
        if (message is null || (isInternal && !await access.HasPermissionAsync(SupportModule.Permissions.InternalRead, cancellationToken)))
        {
            return RecordResult<TicketAttachmentItem>.Fail(RecordErrorCode.NotFound, "Mesaj bulunamadı.");
        }

        if (!await access.HasPermissionAsync(permission, cancellationToken))
        {
            return RecordResult<TicketAttachmentItem>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var status = TicketWorkflow.Text(ticket.Value!, "status") ?? TicketStatus.New;
        if (TicketStatus.IsFinal(status))
        {
            return RecordResult<TicketAttachmentItem>.Fail(RecordErrorCode.Conflict, "Kapanmış, iptal edilmiş ya da birleştirilmiş talebe ek eklenemez.", [new RecordFieldError("status", "Talep kapalı.")]);
        }

        var (name, contentType, error) = AttachmentRules.Validate(fileName, content);
        if (error is not null)
        {
            return Invalid("file", error);
        }

        var perMessage = await context.Data.Set<TicketAttachment>().CountAsync(a => a.MessageId == messageId, cancellationToken);
        if (perMessage >= AttachmentRules.MaxPerMessage)
        {
            return Invalid("file", $"Bir mesaja en fazla {AttachmentRules.MaxPerMessage} ek eklenebilir.");
        }

        if (await context.Data.Set<TicketAttachment>().CountAsync(a => a.TicketId == ticketId, cancellationToken) >= AttachmentRules.MaxPerTicket)
        {
            return Invalid("file", $"Bir talebe en fazla {AttachmentRules.MaxPerTicket} ek eklenebilir.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(content));
        if (await context.Data.Set<TicketAttachment>().AnyAsync(a => a.MessageId == messageId && a.Sha256 == hash, cancellationToken))
        {
            return RecordResult<TicketAttachmentItem>.Fail(RecordErrorCode.Conflict, "Bu dosya mesaja zaten eklenmiş.", [new RecordFieldError("file", "Aynı içerik zaten ekli.")]);
        }

        TicketAttachment? created = null;
        Func<SupportStoreContext, SupportTicket, CancellationToken, Task> effect = (ctx, t, ct) =>
        {
            created = new TicketAttachment
            {
                TicketId = t.Id,
                MessageId = messageId,
                FileName = name!,
                ContentType = contentType!,
                SizeBytes = content.Length,
                Sha256 = hash,
                Content = content,
                CreatedBy = ctx.CurrentUserId,
            };
            ctx.Data.Set<TicketAttachment>().Add(created);
            return Task.CompletedTask;
        };

        var kind = isInternal ? TicketEventKinds.InternalAttachmentAdded : TicketEventKinds.AttachmentAdded;
        var values = new JsonObject { ["lastActivityAt"] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
        using (gate.Open([permission], kind, name, effect))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, null) { Reason = $"support.{kind}" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<TicketAttachmentItem>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        return RecordResult<TicketAttachmentItem>.Ok(ToItem(created!, isInternal));
    }

    public async Task<RecordResult<TicketAttachmentFile>> GetAsync(Guid ticketId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<TicketAttachmentFile>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var includeInternal = await access.HasPermissionAsync(SupportModule.Permissions.InternalRead, cancellationToken);
        var row = await (from a in context.Data.Set<TicketAttachment>().AsNoTracking()
                         join m in context.Data.Set<TicketMessage>().AsNoTracking() on a.MessageId equals m.Id
                         where a.Id == attachmentId && a.TicketId == ticketId && (includeInternal || m.Visibility == MessageVisibility.Public)
                         select new { Attachment = a, m.Visibility }).FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? RecordResult<TicketAttachmentFile>.Fail(RecordErrorCode.NotFound, "Ek bulunamadı.")
            : RecordResult<TicketAttachmentFile>.Ok(new TicketAttachmentFile(ToItem(row.Attachment, row.Visibility == MessageVisibility.Internal), row.Attachment.Content));
    }

    private async Task<IReadOnlyList<TicketAttachmentItem>> QueryAsync(Guid ticketId, bool includeInternal, CancellationToken cancellationToken)
    {
        var rows = await (from a in context.Data.Set<TicketAttachment>().AsNoTracking()
                          join m in context.Data.Set<TicketMessage>().AsNoTracking() on a.MessageId equals m.Id
                          where a.TicketId == ticketId && (includeInternal || m.Visibility == MessageVisibility.Public)
                          orderby a.CreatedAt, a.Id
                          select new
                          {
                              a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.ScanStatus, a.CreatedBy, a.CreatedAt, m.Visibility,
                          }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => new TicketAttachmentItem(
            r.Id, r.MessageId, r.FileName, r.ContentType, r.SizeBytes, r.Sha256, r.ScanStatus, r.Visibility == MessageVisibility.Internal, r.CreatedBy, r.CreatedAt))];
    }

    private static TicketAttachmentItem ToItem(TicketAttachment a, bool isInternal) =>
        new(a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.ScanStatus, isInternal, a.CreatedBy, a.CreatedAt);

    private static RecordResult<TicketAttachmentItem> Invalid(string field, string message) =>
        RecordResult<TicketAttachmentItem>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
