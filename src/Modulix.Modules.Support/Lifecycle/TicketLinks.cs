using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>Bağlanabilen kayıt türleri. Destek bu modüllerin tablolarını görmez; kayıtlar anahtar ve kimlikle, kayıt boru hattından okunur.</summary>
public static class TicketLinkKinds
{
    public const string Contract = "contracts.contract";
    public const string Order = "orders.order";
    public const string Delivery = "orders.delivery";

    public static readonly IReadOnlyList<string> All = [Contract, Order, Delivery];

    public static string Name(string entityKey) => entityKey switch
    {
        Contract => "Sözleşme",
        Order => "Sipariş",
        Delivery => "Teslimat",
        _ => entityKey,
    };
}

public sealed record TicketLinkItem(Guid Id, string EntityKey, Guid RecordId, string Label, Guid? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Talebin ilişkili kayıtları (ADR-0024 dilim 5). Bağlama yetkisi talep düzenleme iznidir; hedef kayıt <b>çağıranın kendi yetkisi ve veri kapsamıyla</b> kayıt boru hattından okunur
/// (okunamıyorsa, modül kapalıysa ya da kayıt yoksa aynı yanıt verilir: varlığı sızmaz), talebin müşterisiyle aynı müşteriye ait olmalıdır (talebe müşteri bağlı değilse bağlanmaz).
/// Sözleşme bağlanınca ya da kaldırılınca talebin SLA politikası aynı işlemde yeniden seçilir (sözleşmeye bağlı politika varsa); ihlal edilmiş örnek değişmez.
/// Bağlama ve kaldırma tarihçeye yazılır. Destek karşı modülde hiçbir şey değiştirmez (kapatma, ödeme, iade yapmaz).
/// </summary>
public interface ITicketLinks
{
    public const int MaxPerTicket = 20;

    Task<RecordResult<IReadOnlyList<TicketLinkItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<TicketLinkItem>>> AddAsync(Guid ticketId, string? entityKey, Guid recordId, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<TicketLinkItem>>> RemoveAsync(Guid ticketId, Guid linkId, CancellationToken cancellationToken);
}

internal sealed class TicketLinks(IRecordPipeline pipeline, TicketWorkflowGate gate, ICurrentAccess access, SupportStoreContext context) : ITicketLinks
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<TicketLinkItem>>> ListAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        return ticket.IsSuccess
            ? RecordResult<IReadOnlyList<TicketLinkItem>>.Ok(await ReadAsync(ticketId, cancellationToken))
            : RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
    }

    public async Task<RecordResult<IReadOnlyList<TicketLinkItem>>> AddAsync(Guid ticketId, string? entityKey, Guid recordId, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard.Error is not null)
        {
            return guard.Error;
        }

        if (entityKey is null || !TicketLinkKinds.All.Contains(entityKey))
        {
            return Invalid("entityKey", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketLinkKinds.All)}.");
        }

        if (recordId == Guid.Empty)
        {
            return Invalid("recordId", "Kayıt seçin.");
        }

        if (guard.PartyId is not { } ticketParty)
        {
            return Invalid("partyId", "İlişkili kayıt bağlamak için önce talebe müşteri bağlayın.");
        }

        if (await context.Data.Set<TicketLink>().AnyAsync(l => l.TicketId == ticketId && l.EntityKey == entityKey && l.RecordId == recordId, cancellationToken))
        {
            return RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(RecordErrorCode.Conflict, "Bu kayıt talebe zaten bağlı.", [new RecordFieldError("recordId", "Zaten bağlı.")]);
        }

        if (await context.Data.Set<TicketLink>().CountAsync(l => l.TicketId == ticketId, cancellationToken) >= ITicketLinks.MaxPerTicket)
        {
            return Invalid("recordId", $"Bir talebe en fazla {ITicketLinks.MaxPerTicket} kayıt bağlanabilir.");
        }

        var resolved = await ResolveAsync(entityKey, recordId, cancellationToken);
        if (resolved is null)
        {
            return Invalid("recordId", "Kayıt bulunamadı, erişim yetkiniz yok ya da ilgili modül kapalı.");
        }

        if (resolved.PartyId != ticketParty)
        {
            return RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(
                RecordErrorCode.Conflict, "Farklı müşteriye ait kayıt talebe bağlanamaz.", [new RecordFieldError("recordId", "Kayıt talebin müşterisine ait değil.")]);
        }

        var isContract = entityKey == TicketLinkKinds.Contract;
        var reason = $"{TicketLinkKinds.Name(entityKey)} {resolved.Label}" + (isContract ? " (SLA politikası yeniden seçilir)" : string.Empty);
        return await WriteAsync(ticketId, TicketEventKinds.LinkAdded, reason, async (ctx, ticket, ct) =>
        {
            ctx.Data.Set<TicketLink>().Add(new TicketLink { TicketId = ticket.Id, EntityKey = entityKey, RecordId = recordId, Label = resolved.Label, CreatedBy = ctx.CurrentUserId });
            if (isContract)
            {
                await SlaEngine.OnLinksChangedAsync(ctx, ticket, added: recordId, removed: null, ct);
            }
        }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<TicketLinkItem>>> RemoveAsync(Guid ticketId, Guid linkId, CancellationToken cancellationToken)
    {
        var guard = await GuardAsync(ticketId, cancellationToken);
        if (guard.Error is not null)
        {
            return guard.Error;
        }

        var link = await context.Data.Set<TicketLink>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId && l.TicketId == ticketId, cancellationToken);
        if (link is null)
        {
            return RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(RecordErrorCode.NotFound, "Bağlantı bulunamadı.");
        }

        var isContract = link.EntityKey == TicketLinkKinds.Contract;
        var reason = $"{TicketLinkKinds.Name(link.EntityKey)} {link.Label}" + (isContract ? " (SLA politikası yeniden seçilir)" : string.Empty);
        return await WriteAsync(ticketId, TicketEventKinds.LinkRemoved, reason, async (ctx, ticket, ct) =>
        {
            var tracked = await ctx.Data.Set<TicketLink>().SingleAsync(l => l.Id == linkId && l.TicketId == ticket.Id, ct);
            ctx.Data.Set<TicketLink>().Remove(tracked);
            if (isContract)
            {
                await SlaEngine.OnLinksChangedAsync(ctx, ticket, added: null, removed: link.RecordId, ct);
            }
        }, cancellationToken);
    }

    private sealed record Resolved(string Label, Guid? PartyId);

    /// <summary>Hedef kaydı çağıranın yetkisiyle okur; etiketi ve müşterisini döner. Teslimatın müşterisi siparişinden gelir.</summary>
    private async Task<Resolved?> ResolveAsync(string entityKey, Guid recordId, CancellationToken cancellationToken)
    {
        var record = await context.ReadRecordAsync(entityKey, recordId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var number = Text(record, "number");
        if (number is null)
        {
            return null;
        }

        if (entityKey != TicketLinkKinds.Delivery)
        {
            var title = Text(record, "title");
            return new Resolved(Truncate(title is null ? number : $"{number} · {title}"), IdOf(record, "partyId"));
        }

        if (IdOf(record, "orderId") is not { } orderId || await context.ReadRecordAsync(TicketLinkKinds.Order, orderId, cancellationToken) is not { } order)
        {
            return null;
        }

        return new Resolved(Truncate($"{number} · {Text(order, "number")}"), IdOf(order, "partyId"));
    }

    private static string? Text(RecordData record, string field) => record.Values[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static Guid? IdOf(RecordData record, string field) => Text(record, field) is { } s && System.Guid.TryParse(s, out var id) ? id : null;

    private static string Truncate(string label) => label.Length > TicketLink.LabelMaxLength ? label[..TicketLink.LabelMaxLength] : label;

    private async Task<(RecordResult<IReadOnlyList<TicketLinkItem>>? Error, Guid? PartyId)> GuardAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        // Önce kapsamla okunur (kapsam dışı 404), sonra izin (403), sonra durum (409).
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return (RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors), null);
        }

        if (!await access.HasPermissionAsync(SupportModule.Permissions.TicketsWrite, cancellationToken))
        {
            return (RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.TicketsWrite}' izni gerekir."), null);
        }

        var status = TicketWorkflow.Text(ticket.Value!, "status") ?? TicketStatus.New;
        if (TicketStatus.IsFinal(status))
        {
            return (RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(RecordErrorCode.Conflict, "Kapanmış, iptal edilmiş ya da birleştirilmiş talebin bağlantıları değiştirilemez.", [new RecordFieldError("status", "Talep kapalı.")]), null);
        }

        return (null, TicketWorkflow.Text(ticket.Value!, "partyId") is { } party && System.Guid.TryParse(party, out var partyId) ? partyId : null);
    }

    private async Task<RecordResult<IReadOnlyList<TicketLinkItem>>> WriteAsync(
        Guid ticketId, string kind, string reason, Func<SupportStoreContext, SupportTicket, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        var values = new JsonObject { ["lastActivityAt"] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
        using (gate.Open([SupportModule.Permissions.TicketsWrite], kind, reason.Length > SupportTicket.ReasonMaxLength ? reason[..SupportTicket.ReasonMaxLength] : reason, effect))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, null) { Reason = $"support.{kind}" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        return RecordResult<IReadOnlyList<TicketLinkItem>>.Ok(await ReadAsync(ticketId, cancellationToken));
    }

    private async Task<IReadOnlyList<TicketLinkItem>> ReadAsync(Guid ticketId, CancellationToken cancellationToken) =>
        [.. (await context.Data.Set<TicketLink>().AsNoTracking().Where(l => l.TicketId == ticketId).OrderBy(l => l.CreatedAt).ToListAsync(cancellationToken))
            .Select(l => new TicketLinkItem(l.Id, l.EntityKey, l.RecordId, l.Label, l.CreatedBy, l.CreatedAt))];

    private static RecordResult<IReadOnlyList<TicketLinkItem>> Invalid(string field, string message) =>
        RecordResult<IReadOnlyList<TicketLinkItem>>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
