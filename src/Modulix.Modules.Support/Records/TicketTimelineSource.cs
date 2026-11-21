using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Contracts.Timeline;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Records;

/// <summary>
/// Müşteri zaman çizelgesinin destek talebi kaynağı (ADR-0053, dilim 2c): müşterinin talepleri, açılış anıyla. Talepler ortak taraf kaydına
/// (<c>partyId</c>) bağlıdır; satırlar kayıt boru hattından okunur, izin, veri kapsamı ve lisans orada uygulanır. Boru hattı oluşturulma anını
/// dışarı vermediği için o an, boru hattının görünür bulduğu kimliklerle modülün kendi tablosundan okunur.
/// </summary>
internal sealed class TicketTimelineSource(IRecordPipeline pipeline, IModuleData data) : ICustomerTimelineSource
{
    public string Key => "support.tickets";

    public async Task<CustomerTimelineSlice?> ReadAsync(CustomerTimelineQuery query, CancellationToken cancellationToken)
    {
        if (query.PartyId is not { } partyId)
        {
            // Ortak taraf kaydı olmayan firmanın talebi olamaz: okunabilir, boş kaynak.
            return new CustomerTimelineSlice([], false);
        }

        var filters = new Dictionary<string, string> { ["partyId"] = partyId.ToString() };
        if (query.Before is { } before)
        {
            filters["createdBefore"] = before.ToString("O", CultureInfo.InvariantCulture);
        }

        var take = Math.Clamp(query.Take, 1, RecordQuery.MaxPageSize);
        var page = await pipeline.QueryAsync(new RecordQuery(SupportModule.Entities.Ticket, 1, take, Filters: filters), cancellationToken);
        if (!page.IsSuccess)
        {
            // Çağıran talepleri okuyamıyorsa kaynak yoktur; başka hata sessizce boş çizelgeye çevrilmez.
            return page.Error!.Code is RecordErrorCode.PermissionDenied or RecordErrorCode.FeatureNotLicensed
                ? null
                : throw new InvalidOperationException($"Zaman çizelgesi okunamadı ({Key}): {page.Error.Message}");
        }

        var items = page.Value!.Items;
        var ids = items.Select(i => i.Id).ToList();
        var created = await data.Set<SupportTicket>().AsNoTracking().Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.CreatedAt, cancellationToken);
        return new CustomerTimelineSlice(
            [
                .. items.Select(i => new CustomerTimelineEntry(
                    SupportModule.Entities.Ticket,
                    i.Id,
                    created[i.Id],
                    Text(i.Values, "subject") ?? string.Empty,
                    Text(i.Values, "status"),
                    OwnerId: Guid.TryParse(Text(i.Values, OwnedRecordType.OwnerField), out var owner) ? owner : null,
                    Reference: Text(i.Values, "number"))),
            ],
            page.Value.TotalCount > items.Count);
    }

    private static string? Text(JsonObject values, string name) => values[name]?.GetValue<string>();
}
