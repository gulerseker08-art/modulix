using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>
/// Mükerrer talepleri birleştirir (ADR-0024 dilim 3). Kaynak talep <c>Merged</c> (son durum) olur ve hedefe işaret eder; <b>veri kaybolmaz</b>: kaynağın tarihçesi, mesajları, dış anahtarı
/// (<c>ClientKey</c>) ve SLA kayıtları kaynakta kalır, kaynağın mesajları özgün zamanlarıyla hedefe kopyalanır (iç not görünürlüğü korunur), hedefte "birleştirme alındı" tarihçe satırı oluşur.
/// Kaynağın açık SLA örnekleri iptal edilir; hedefin SLA'sı ve ilk yanıt zamanı değişmez. Farklı müşterilerin talepleri birleştirilmez. Kaynak ve hedef çağıranın görebildiği talepler olmalıdır.
/// Birleştirme ayrı yetki (<c>support.tickets.merge</c>) ve gerekçe ister; geri alınamaz (hedef talep yeniden açma/ilişkili talep açma ile devam edilir).
/// </summary>
public interface ITicketMerge
{
    Task<RecordResult<RecordData>> MergeAsync(Guid sourceId, Guid targetId, long version, string? reason, CancellationToken cancellationToken);
}

internal sealed class TicketMerge(IRecordPipeline pipeline, TicketWorkflowGate gate, ICurrentAccess access, SupportStoreContext context) : ITicketMerge
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<RecordData>> MergeAsync(Guid sourceId, Guid targetId, long version, string? reason, CancellationToken cancellationToken)
    {
        // Önce kapsamla okunur (kapsam dışı kaynak 404), sonra izin (403), gerekçe ve hedef (422), sonra kural (409).
        var source = await pipeline.GetAsync(new RecordRef(Key, sourceId), cancellationToken);
        if (!source.IsSuccess)
        {
            return source;
        }

        var permission = SupportModule.Permissions.TicketsMerge;
        if (!await access.HasPermissionAsync(permission, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
        {
            return Invalid("reason", "Gerekçe zorunludur.");
        }

        var target = await pipeline.GetAsync(new RecordRef(Key, targetId), cancellationToken);
        if (!target.IsSuccess)
        {
            return Invalid("targetId", "Hedef talep bulunamadı ya da erişim yetkiniz yok.");
        }

        var rule = MergeRules.Can(FactsOf(source.Value!), FactsOf(target.Value!));
        if (!rule.Allowed)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu talepler birleştirilemez.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        var targetNumber = TicketWorkflow.Text(target.Value!, "number");
        var sourceNumber = TicketWorkflow.Text(source.Value!, "number");
        var values = new JsonObject
        {
            ["status"] = TicketStatus.Merged,
            ["mergedIntoId"] = targetId.ToString(),
            ["statusReason"] = Truncate($"{targetNumber} talebine birleştirildi: {text}"),
            ["lastActivityAt"] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
        };

        using (gate.Open([permission], TicketEventKinds.MergedInto, Truncate($"{targetNumber}: {text}"), async (ctx, ticket, ct) =>
        {
            // Hedef talep izlenerek yüklenir (aynı kaydetmede güncellenir); kaynağın mesajları özgün zamanlarıyla kopyalanır.
            var targetEntity = await ctx.Data.Set<SupportTicket>().SingleAsync(t => t.Id == targetId, ct);
            var messages = await ctx.Data.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == ticket.Id && m.SourceTicketId == null).OrderBy(m => m.CreatedAt).ToListAsync(ct);
            foreach (var m in messages)
            {
                ctx.Data.Set<TicketMessage>().Add(new TicketMessage
                {
                    TicketId = targetEntity.Id,
                    SourceTicketId = ticket.Id,
                    Visibility = m.Visibility,
                    Direction = m.Direction,
                    Body = m.Body,
                    AuthorId = m.AuthorId,
                    DeliveryStatus = m.DeliveryStatus,
                    IsAutomatic = m.IsAutomatic,
                    CreatedAt = m.CreatedAt,
                });
            }

            targetEntity.LastActivityAt = ctx.Time.GetUtcNow();
            targetEntity.Touch();
            TicketHistory.Add(ctx, targetEntity, TicketEventKinds.MergeReceived, Truncate($"{sourceNumber} birleştirildi ({messages.Count} mesaj kopyalandı): {text}"), targetEntity.Status, targetEntity.OwnerId, targetEntity.QueueId);
        }))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, sourceId, values, version) { Reason = "support.MergedInto" }, cancellationToken);
        }
    }

    private static MergeFacts FactsOf(RecordData t) =>
        new(t.Id, TicketWorkflow.Text(t, "status") ?? TicketStatus.New, TicketWorkflow.Id(t, "partyId"), TicketWorkflow.Text(t, "reporterEmail"), TicketWorkflow.Text(t, "reporterPhone"));

    private static string Truncate(string value) => value.Length > SupportTicket.ReasonMaxLength ? value[..SupportTicket.ReasonMaxLength] : value;

    private static RecordResult<RecordData> Invalid(string field, string message) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
