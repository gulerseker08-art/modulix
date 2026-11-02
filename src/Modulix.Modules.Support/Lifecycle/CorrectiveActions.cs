using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record CorrectiveActionItem(
    Guid Id, string Title, Guid OwnerId, DateOnly DueDate, string Status, bool IsOverdue, int DaysOverdue, string? Note, DateTimeOffset? ClosedAt, Guid? ClosedBy, Guid? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Şikâyetin düzeltici aksiyonları (ADR-0024 dilim 6): kök nedene karşı izlenebilir iş. Yalnızca şikâyet türündeki talepte, iptal edilmemiş ve birleştirilmemiş talepte açılır; çözülmüş/kapanmış
/// şikâyette de açılabilir (aksiyon çoğu zaman çözümden sonra yürür). Yazma ayrı yetki ister (<c>support.complaints.manage</c>) ve talebin görünürlüğü içinde yapılır; okuma talep okuma iznine bağlıdır.
/// Sorumlu etkin üye olmalı, son tarih geçmiş olamaz; tamamlama ve iptal sonuç/gerekçe notu ister; aksiyon silinmez, yeniden açılabilir. Her değişiklik talebin tarihçesine yazılır.
/// Gecikme: açık ve son tarihi (Türkiye günü) geçmiş.
/// </summary>
public interface ICorrectiveActions
{
    Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> AddAsync(Guid ticketId, string? title, Guid? ownerId, DateOnly? dueDate, CancellationToken cancellationToken);

    /// <param name="status"><see cref="CorrectiveActionStatus.Done"/>, <see cref="CorrectiveActionStatus.Cancelled"/> ya da yeniden açmak için <see cref="CorrectiveActionStatus.Open"/>.</param>
    Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> SetStatusAsync(Guid ticketId, Guid actionId, string? status, string? note, CancellationToken cancellationToken);
}

internal sealed class CorrectiveActions(IRecordPipeline pipeline, TicketWorkflowGate gate, ICurrentAccess access, SupportStoreContext context) : ICorrectiveActions
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        return ticket.IsSuccess
            ? RecordResult<IReadOnlyList<CorrectiveActionItem>>.Ok(await ReadAsync(ticketId, cancellationToken))
            : RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
    }

    public async Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> AddAsync(Guid ticketId, string? title, Guid? ownerId, DateOnly? dueDate, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        var text = title?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Invalid("title", "Aksiyon başlığı zorunludur.");
        }

        if (text.Length > CorrectiveAction.TitleMaxLength)
        {
            return Invalid("title", $"En fazla {CorrectiveAction.TitleMaxLength} karakter olabilir.");
        }

        if (ownerId is not { } owner || !await context.Members.IsActiveMemberAsync(owner, cancellationToken))
        {
            return Invalid("ownerId", "Sorumlu zorunludur ve bu kuruluşun etkin üyesi olmalıdır.");
        }

        var today = ReportPeriod.LocalDate(context.Time.GetUtcNow());
        if (dueDate is not { } due)
        {
            return Invalid("dueDate", "Son tarih zorunludur.");
        }

        if (due < today)
        {
            return Invalid("dueDate", "Son tarih geçmişte olamaz.");
        }

        if (due > today.AddYears(2))
        {
            return Invalid("dueDate", "Son tarih en çok 2 yıl sonrası olabilir.");
        }

        if (await context.Data.Set<CorrectiveAction>().CountAsync(a => a.TicketId == ticketId, cancellationToken) >= CorrectiveAction.MaxPerTicket)
        {
            return Invalid("title", $"Bir talepte en fazla {CorrectiveAction.MaxPerTicket} düzeltici aksiyon olabilir.");
        }

        return await WriteAsync(ticketId, TicketEventKinds.CorrectiveActionAdded, $"{text} (son tarih {due:dd.MM.yyyy})", (ctx, ticket, ct) =>
        {
            ctx.Data.Set<CorrectiveAction>().Add(new CorrectiveAction { TicketId = ticket.Id, Title = text, OwnerId = owner, DueDate = due, CreatedBy = ctx.CurrentUserId });
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> SetStatusAsync(Guid ticketId, Guid actionId, string? status, string? note, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        if (status is null || !CorrectiveActionStatus.All.Contains(status))
        {
            return Invalid("status", $"Şunlardan biri olmalıdır: {string.Join(", ", CorrectiveActionStatus.All)}.");
        }

        var action = await context.Data.Set<CorrectiveAction>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == actionId && a.TicketId == ticketId, cancellationToken);
        if (action is null)
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(RecordErrorCode.NotFound, "Düzeltici aksiyon bulunamadı.");
        }

        if (action.Status == status)
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(RecordErrorCode.Conflict, "Aksiyon zaten bu durumda.", [new RecordFieldError("status", "Durum değişmedi.")]);
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is null)
        {
            return Invalid("note", status == CorrectiveActionStatus.Done ? "Sonuç notu zorunludur (ne yapıldı, nasıl doğrulandı)." : "Gerekçe zorunludur.");
        }

        if (text.Length > CorrectiveAction.NoteMaxLength)
        {
            return Invalid("note", $"En fazla {CorrectiveAction.NoteMaxLength} karakter olabilir.");
        }

        var kind = status switch
        {
            CorrectiveActionStatus.Done => TicketEventKinds.CorrectiveActionDone,
            CorrectiveActionStatus.Cancelled => TicketEventKinds.CorrectiveActionCancelled,
            _ => TicketEventKinds.CorrectiveActionReopened,
        };
        return await WriteAsync(ticketId, kind, $"{action.Title}: {text}", async (ctx, ticket, ct) =>
        {
            var tracked = await ctx.Data.Set<CorrectiveAction>().SingleAsync(a => a.Id == actionId && a.TicketId == ticket.Id, ct);
            tracked.Status = status;
            tracked.Note = text;
            tracked.ClosedAt = status == CorrectiveActionStatus.Open ? null : ctx.Time.GetUtcNow();
            tracked.ClosedBy = status == CorrectiveActionStatus.Open ? null : ctx.CurrentUserId;
            tracked.Touch();
        }, cancellationToken);
    }

    /// <summary>Önce kapsamla okunur (kapsam dışı 404), sonra izin (403), sonra tür ve durum (409). Hata yoksa null.</summary>
    private async Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>?> GuardAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        if (!await access.HasPermissionAsync(SupportModule.Permissions.ComplaintsManage, cancellationToken))
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.ComplaintsManage}' izni gerekir.");
        }

        var typeKey = TicketWorkflow.Text(ticket.Value!, "typeKey");
        var types = await context.ChoiceLists.GetAsync(SupportModule.Lists.TicketType, cancellationToken);
        if (typeKey is null || types.Find(typeKey)?.Category != SupportModule.TypeCategories.Complaint)
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(
                RecordErrorCode.Conflict, "Düzeltici aksiyon yalnızca şikâyet türündeki talepte açılır.", [new RecordFieldError("typeKey", "Talep şikâyet türünde değil.")]);
        }

        var status = TicketWorkflow.Text(ticket.Value!, "status") ?? TicketStatus.New;
        if (status is TicketStatus.Cancelled or TicketStatus.Merged)
        {
            return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(
                RecordErrorCode.Conflict, "İptal edilmiş ya da birleştirilmiş talepte düzeltici aksiyon değiştirilemez.", [new RecordFieldError("status", "Talep kapalı.")]);
        }

        return null;
    }

    private async Task<RecordResult<IReadOnlyList<CorrectiveActionItem>>> WriteAsync(
        Guid ticketId, string kind, string reason, Func<SupportStoreContext, SupportTicket, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        var values = new JsonObject { ["lastActivityAt"] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
        using (gate.Open([SupportModule.Permissions.ComplaintsManage], kind, reason.Length > SupportTicket.ReasonMaxLength ? reason[..SupportTicket.ReasonMaxLength] : reason, effect))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, null) { Reason = $"support.{kind}" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        return RecordResult<IReadOnlyList<CorrectiveActionItem>>.Ok(await ReadAsync(ticketId, cancellationToken));
    }

    private async Task<IReadOnlyList<CorrectiveActionItem>> ReadAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var today = ReportPeriod.LocalDate(context.Time.GetUtcNow());
        var rows = await context.Data.Set<CorrectiveAction>().AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync(cancellationToken);
        return [.. rows.OrderBy(a => a.Status != CorrectiveActionStatus.Open).ThenBy(a => a.DueDate).ThenBy(a => a.CreatedAt).Select(a => new CorrectiveActionItem(
            a.Id, a.Title, a.OwnerId, a.DueDate, a.Status, CorrectiveActionStatus.IsOverdue(a.Status, a.DueDate, today),
            a.Status == CorrectiveActionStatus.Open ? CorrectiveActionStatus.DaysOverdue(a.DueDate, today) : 0, a.Note, a.ClosedAt, a.ClosedBy, a.CreatedBy, a.CreatedAt))];
    }

    private static RecordResult<IReadOnlyList<CorrectiveActionItem>> Invalid(string field, string message) =>
        RecordResult<IReadOnlyList<CorrectiveActionItem>>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
