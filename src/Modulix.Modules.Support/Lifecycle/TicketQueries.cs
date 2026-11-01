using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>Tarihçe satırı (API görünümü). Mesaj gövdesi içermez.</summary>
public sealed record TicketHistoryItem(
    Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, Guid? FromOwnerId, Guid? ToOwnerId,
    Guid? FromQueueId, Guid? ToQueueId, string? Reason);

/// <summary>
/// Çalışma alanı sayıları. Her sayı kayıt boru hattının listesinden gelir; bu yüzden kullanıcının izni ve veri kapsamı dışındaki talepler
/// sayıya girmez ve sayı, listede aynı süzgeçle açılan sonuçla birebir aynıdır.
/// </summary>
public sealed record TicketSummaryCounts(int Mine, int Unassigned, int AwaitingFirstResponse, int WaitingCustomer, int Reopened, int Resolved, int Open, int SlaAtRisk = 0, int SlaBreached = 0);

/// <summary>Talep okuma sorguları. Görünürlük (izin ve veri kapsamı) kayıt boru hattında doğrulanır; görünmeyen talep "bulunamadı" alır.</summary>
public interface ITicketQueries
{
    Task<RecordResult<IReadOnlyList<TicketHistoryItem>>> HistoryAsync(Guid ticketId, CancellationToken cancellationToken);
}

public interface ITicketSummary
{
    Task<RecordResult<TicketSummaryCounts>> GetAsync(CancellationToken cancellationToken);
}

internal sealed class TicketQueries(IRecordPipeline pipeline, SupportReader reader, ICurrentAccess access) : ITicketQueries
{
    public async Task<RecordResult<IReadOnlyList<TicketHistoryItem>>> HistoryAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(SupportModule.Entities.Ticket, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<TicketHistoryItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        // İç not olayı da iç nottur: iç not okuma izni olmayan kullanıcıya tarihçede gösterilmez.
        var includeInternal = (await access.GetPermissionsAsync(cancellationToken)).Contains(SupportModule.Permissions.InternalRead);
        var events = await reader.EventsAsync(ticketId, cancellationToken);
        return RecordResult<IReadOnlyList<TicketHistoryItem>>.Ok(
        [
            .. events.Where(e => includeInternal || e.Kind != TicketEventKinds.InternalNote)
                .Select(e => new TicketHistoryItem(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.FromOwnerId, e.ToOwnerId, e.FromQueueId, e.ToQueueId, e.Reason)),
        ]);
    }
}

internal sealed class TicketSummaryService(IRecordPipeline pipeline) : ITicketSummary
{
    public async Task<RecordResult<TicketSummaryCounts>> GetAsync(CancellationToken cancellationToken)
    {
        var mine = await CountAsync(cancellationToken, ("ownerId", "me"), ("open", "true"));
        if (mine.Error is { } error)
        {
            return RecordResult<TicketSummaryCounts>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var unassigned = await CountAsync(cancellationToken, ("unassigned", "true"));
        var awaiting = await CountAsync(cancellationToken, ("awaitingFirstResponse", "true"));
        var waiting = await CountAsync(cancellationToken, ("status", TicketStatus.WaitingCustomer));
        var reopened = await CountAsync(cancellationToken, ("status", TicketStatus.Reopened));
        var resolved = await CountAsync(cancellationToken, ("status", TicketStatus.Resolved));
        var open = await CountAsync(cancellationToken, ("open", "true"));
        var atRisk = await CountAsync(cancellationToken, ("slaAtRisk", "true"), ("open", "true"));
        var breached = await CountAsync(cancellationToken, ("slaBreached", "true"), ("open", "true"));
        return RecordResult<TicketSummaryCounts>.Ok(new TicketSummaryCounts(
            mine.Count, unassigned.Count, awaiting.Count, waiting.Count, reopened.Count, resolved.Count, open.Count, atRisk.Count, breached.Count));
    }

    private async Task<(int Count, RecordError? Error)> CountAsync(CancellationToken cancellationToken, params (string Name, string Value)[] filters)
    {
        var result = await pipeline.QueryAsync(
            new RecordQuery(SupportModule.Entities.Ticket, 1, 1, null, filters.ToDictionary(f => f.Name, f => f.Value)), cancellationToken);
        return result.IsSuccess ? (result.Value!.TotalCount, null) : (0, result.Error);
    }
}
