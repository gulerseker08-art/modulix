using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Records;

/// <summary>
/// Destek modülünün okuma sorguları (tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır). Görünürlük (izin ve veri
/// kapsamı) çağıran serviste kayıt boru hattıyla doğrulanır; bu sınıf yalnızca görünür talebin ek verisini okur.
/// </summary>
internal sealed class SupportReader(IModuleData data)
{
    /// <summary>Talebin mesajları, eskiden yeniye. <paramref name="includeInternal"/> false ise iç notlar hiç okunmaz.</summary>
    public async Task<List<TicketMessage>> MessagesAsync(Guid ticketId, bool includeInternal, CancellationToken cancellationToken)
    {
        var query = data.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == ticketId);
        if (!includeInternal)
        {
            query = query.Where(m => m.Visibility == Lifecycle.MessageVisibility.Public);
        }

        return await query.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(cancellationToken);
    }

    public async Task<List<TicketEvent>> EventsAsync(Guid ticketId, CancellationToken cancellationToken) =>
        await data.Set<TicketEvent>().AsNoTracking().Where(e => e.TicketId == ticketId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Yanıtın gideceği e-posta: başvuran e-postası, yoksa kişinin, yoksa müşterinin e-postası.</summary>
    public async Task<(string? Address, string? Name)> RecipientAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ticket.ReporterEmail))
        {
            return (ticket.ReporterEmail, ticket.ReporterName);
        }

        if (ticket.PersonId is { } personId
            && await data.Set<Modulix.Core.Contracts.SharedRecords.Person>().AsNoTracking().Where(p => p.Id == personId && p.Email != null)
                .Select(p => new { p.Email, Name = p.FirstName + " " + p.LastName }).FirstOrDefaultAsync(cancellationToken) is { } person)
        {
            return (person.Email, person.Name);
        }

        if (ticket.PartyId is { } partyId
            && await data.Set<Modulix.Core.Contracts.SharedRecords.Party>().AsNoTracking().Where(p => p.Id == partyId && p.Email != null)
                .Select(p => new { p.Email, p.Name }).FirstOrDefaultAsync(cancellationToken) is { } party)
        {
            return (party.Email, party.Name);
        }

        return (null, null);
    }
}

/// <summary>
/// Talebin aktivite ilişkisi hedefi (<see cref="IActivityLinkTarget"/>, ADR-0015): takip görevi ve görüşmeler talebe bağlanabilir.
/// Görünürlük kayıt boru hattının kuralıyla aynıdır (okuma izni + veri kapsamı; atanmamış talep kapsamda görünür). Başlık yalnızca konu ve numaradır.
/// </summary>
internal sealed class TicketLinkTarget(IModuleData data, IRecordAccess access) : IActivityLinkTarget
{
    public string EntityKey => SupportModule.Entities.Ticket;

    public async Task<bool> IsVisibleAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(EntityKey, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var query = data.Set<SupportTicket>().AsNoTracking().Where(t => t.Id == recordId);
        if (await access.GetOwnersAsync(cancellationToken) is { } owners)
        {
            var ids = owners.Select(id => (Guid?)id).ToList();
            query = query.Where(t => t.OwnerId == null || ids.Contains(t.OwnerId));
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<string?> GetTitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        await IsVisibleAsync(recordId, cancellationToken)
            ? await data.Set<SupportTicket>().AsNoTracking().Where(t => t.Id == recordId).Select(t => t.Subject + " (" + t.Number + ")").SingleOrDefaultAsync(cancellationToken)
            : null;
}
