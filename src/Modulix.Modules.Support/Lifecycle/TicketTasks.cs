using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record TicketTaskItem(
    Guid Id, string Title, Guid? AssigneeId, bool IsBlocking, bool IsDone, DateTimeOffset? DoneAt, Guid? DoneBy, Guid? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Talep alt işleri (ADR-0024 dilim 3). Engelleyici açık alt iş varken talep çözülemez (sunucuda uygulanır). Alt iş talebin güncellemesiyle aynı işlemde yazılır (son hareket, sürüm,
/// tarihçe); silinmez, yalnızca tamamlanır ya da yeniden açılır. Yazma talep düzenleme izniyle ve talebin görünürlüğü içinde yapılır. Takip görevi/görüşme için aktivite altyapısı kullanılır.
/// </summary>
public interface ITicketTasks
{
    Task<RecordResult<IReadOnlyList<TicketTaskItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<TicketTaskItem>>> AddAsync(Guid ticketId, string? title, Guid? assigneeId, bool isBlocking, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<TicketTaskItem>>> SetDoneAsync(Guid ticketId, Guid taskId, bool done, CancellationToken cancellationToken);
}

internal sealed class TicketTasks(IRecordPipeline pipeline, TicketWorkflowGate gate, ICurrentAccess access, SupportStoreContext context) : ITicketTasks
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<TicketTaskItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        return ticket.IsSuccess
            ? RecordResult<IReadOnlyList<TicketTaskItem>>.Ok(await ReadAsync(ticketId, cancellationToken))
            : RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
    }

    public async Task<RecordResult<IReadOnlyList<TicketTaskItem>>> AddAsync(Guid ticketId, string? title, Guid? assigneeId, bool isBlocking, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard.Error is not null)
        {
            return guard.Error;
        }

        var text = title?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Invalid("title", "Alt iş başlığı zorunludur.");
        }

        if (text.Length > TicketTask.TitleMaxLength)
        {
            return Invalid("title", $"En fazla {TicketTask.TitleMaxLength} karakter olabilir.");
        }

        if (!TicketStatus.IsWorking(guard.Status!))
        {
            return RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.Conflict, "Alt iş yalnızca çalışılan talebe eklenebilir.", [new RecordFieldError("status", "Talep çalışılan durumda değil.")]);
        }

        if (assigneeId is { } assignee && !await context.Members.IsActiveMemberAsync(assignee, cancellationToken))
        {
            return Invalid("assigneeId", "Kullanıcı bu kuruluşun üyesi değil.");
        }

        return await WriteAsync(ticketId, TicketEventKinds.TaskAdded, text, (ctx, ticket, ct) =>
        {
            ctx.Data.Set<TicketTask>().Add(new TicketTask { TicketId = ticket.Id, Title = text, AssigneeId = assigneeId, IsBlocking = isBlocking, CreatedBy = ctx.CurrentUserId });
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<TicketTaskItem>>> SetDoneAsync(Guid ticketId, Guid taskId, bool done, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard.Error is not null)
        {
            return guard.Error;
        }

        var task = await context.Data.Set<TicketTask>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId && t.TicketId == ticketId, cancellationToken);
        if (task is null)
        {
            return RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.NotFound, "Alt iş bulunamadı.");
        }

        if (task.IsDone == done)
        {
            return RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.Conflict, done ? "Alt iş zaten tamamlanmış." : "Alt iş zaten açık.", [new RecordFieldError("status", "Durum değişmedi.")]);
        }

        if (!done && !TicketStatus.IsWorking(guard.Status!))
        {
            return RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.Conflict, "Alt iş yalnızca çalışılan talepte yeniden açılabilir.", [new RecordFieldError("status", "Talep çalışılan durumda değil.")]);
        }

        return await WriteAsync(ticketId, done ? TicketEventKinds.TaskCompleted : TicketEventKinds.TaskReopened, task.Title, async (ctx, ticket, ct) =>
        {
            var tracked = await ctx.Data.Set<TicketTask>().SingleAsync(t => t.Id == taskId && t.TicketId == ticket.Id, ct);
            tracked.IsDone = done;
            tracked.DoneAt = done ? ctx.Time.GetUtcNow() : null;
            tracked.DoneBy = done ? ctx.CurrentUserId : null;
            tracked.Touch();
        }, cancellationToken);
    }

    private async Task<(RecordResult<IReadOnlyList<TicketTaskItem>>? Error, string? Status)> GuardAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        // Önce kapsamla okunur (kapsam dışı 404), sonra izin (403).
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return (RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors), null);
        }

        if (!await access.HasPermissionAsync(SupportModule.Permissions.TicketsWrite, cancellationToken))
        {
            return (RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.TicketsWrite}' izni gerekir."), null);
        }

        return (null, TicketWorkflow.Text(ticket.Value!, "status") ?? TicketStatus.New);
    }

    private async Task<RecordResult<IReadOnlyList<TicketTaskItem>>> WriteAsync(
        Guid ticketId, string kind, string reason, Func<SupportStoreContext, SupportTicket, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        var values = new JsonObject { ["lastActivityAt"] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
        using (gate.Open([SupportModule.Permissions.TicketsWrite], kind, reason.Length > SupportTicket.ReasonMaxLength ? reason[..SupportTicket.ReasonMaxLength] : reason, effect))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, null) { Reason = $"support.{kind}" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        return RecordResult<IReadOnlyList<TicketTaskItem>>.Ok(await ReadAsync(ticketId, cancellationToken));
    }

    private async Task<IReadOnlyList<TicketTaskItem>> ReadAsync(Guid ticketId, CancellationToken cancellationToken) =>
        [.. (await context.Data.Set<TicketTask>().AsNoTracking().Where(t => t.TicketId == ticketId).OrderBy(t => t.IsDone).ThenBy(t => t.CreatedAt).ToListAsync(cancellationToken))
            .Select(t => new TicketTaskItem(t.Id, t.Title, t.AssigneeId, t.IsBlocking, t.IsDone, t.DoneAt, t.DoneBy, t.CreatedBy, t.CreatedAt))];

    private static RecordResult<IReadOnlyList<TicketTaskItem>> Invalid(string field, string message) =>
        RecordResult<IReadOnlyList<TicketTaskItem>>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
