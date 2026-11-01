using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Sipariş modülünün okuma sorguları (tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır). Görünürlük (izin ve veri
/// kapsamı) çağıran serviste kayıt boru hattıyla doğrulanır; bu sınıf yalnızca görünür siparişin ek verisini okur.
/// </summary>
internal sealed class OrderReader(IModuleData data)
{
    public async Task<List<SalesOrderEvent>> HistoryAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<SalesOrderEvent>().AsNoTracking().Where(e => e.OrderId == orderId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Teklifin tüketim defteri satırları. Yazmak için izlenir (aynı istekte güncellenip sürüm denetimiyle kaydedilir).</summary>
    public async Task<List<QuoteLineConsumption>> ConsumptionsAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteLineConsumption>().Where(c => c.QuoteId == quoteId).ToListAsync(cancellationToken);

    /// <summary>Siparişin kalemleri (izlenir: aynı istekte güncellenip sürüm denetimiyle kaydedilebilir).</summary>
    public async Task<List<SalesOrderLine>> LinesAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<SalesOrderLine>().Where(l => l.OrderId == orderId).OrderBy(l => l.Position).ToListAsync(cancellationToken);

    public Task<int> ActiveLineCountAsync(Guid orderId, CancellationToken cancellationToken) =>
        data.Set<SalesOrderLine>().AsNoTracking().CountAsync(l => l.OrderId == orderId && !l.IsCancelled, cancellationToken);

    public async Task<List<OrderChangeRequest>> ChangeRequestsAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OrderChangeRequest>().AsNoTracking().Where(c => c.OrderId == orderId).OrderByDescending(c => c.Sequence).ToListAsync(cancellationToken);

    public Task<OrderChangeRequest?> ChangeRequestAsync(Guid id, CancellationToken cancellationToken) =>
        data.Set<OrderChangeRequest>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>Siparişin bekleyen değişiklik talebi (izlenir; sipariş iptalinde geri çekilir).</summary>
    public Task<OrderChangeRequest?> PendingChangeRequestAsync(Guid orderId, CancellationToken cancellationToken) =>
        data.Set<OrderChangeRequest>().SingleOrDefaultAsync(c => c.OrderId == orderId && c.Status == "Pending", cancellationToken);

    /// <summary>Son kaydedilen tarihçe satırının sahibi (ör. onaya gönderen); yoksa null.</summary>
    public async Task<Guid?> LastActorAsync(Guid orderId, string kind, CancellationToken cancellationToken) =>
        await data.Set<SalesOrderEvent>().AsNoTracking().Where(e => e.OrderId == orderId && e.Kind == kind)
            .OrderByDescending(e => e.CreatedAt).Select(e => e.ActorId).FirstOrDefaultAsync(cancellationToken);

    public Task<OrderHandover?> HandoverOfOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        data.Set<OrderHandover>().AsNoTracking().SingleOrDefaultAsync(h => h.OrderId == orderId, cancellationToken);

    public Task<OrderHandover?> HandoverAsync(Guid id, CancellationToken cancellationToken) =>
        data.Set<OrderHandover>().AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<List<OrderHandoverItem>> HandoverItemsAsync(Guid handoverId, CancellationToken cancellationToken) =>
        await data.Set<OrderHandoverItem>().AsNoTracking().Where(i => i.HandoverId == handoverId).OrderBy(i => i.Position).ThenBy(i => i.CreatedAt).ToListAsync(cancellationToken);

    public async Task<List<OrderHandoverEvent>> HandoverEventsAsync(Guid handoverId, CancellationToken cancellationToken) =>
        await data.Set<OrderHandoverEvent>().AsNoTracking().Where(e => e.HandoverId == handoverId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Kabulü belirlenen süreden uzun bekleyen, o tur için gecikme takibi üretilmemiş devirler (zamanlayıcı).</summary>
    public async Task<List<OrderHandover>> HandoversWaitingSinceAsync(DateTimeOffset cutoff, int take, CancellationToken cancellationToken) =>
        await data.Set<OrderHandover>().AsNoTracking()
            .Where(h => h.Status == "Sent" && h.SentAt != null && h.SentAt <= cutoff && h.EscalatedRound < h.Round)
            .OrderBy(h => h.SentAt).Take(take).ToListAsync(cancellationToken);

    public Task<OperationJob?> JobAsync(Guid id, CancellationToken cancellationToken) =>
        data.Set<OperationJob>().AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

    public async Task<List<OperationJobTask>> JobTasksAsync(Guid jobId, CancellationToken cancellationToken) =>
        await data.Set<OperationJobTask>().AsNoTracking().Where(t => t.JobId == jobId).OrderBy(t => t.Position).ThenBy(t => t.CreatedAt).ToListAsync(cancellationToken);

    public async Task<List<OperationJobLine>> JobLinksAsync(Guid jobId, CancellationToken cancellationToken) =>
        await data.Set<OperationJobLine>().AsNoTracking().Where(l => l.JobId == jobId).ToListAsync(cancellationToken);

    public async Task<List<OperationJobEvent>> JobEventsAsync(Guid jobId, CancellationToken cancellationToken) =>
        await data.Set<OperationJobEvent>().AsNoTracking().Where(e => e.JobId == jobId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Siparişin bütün bağımlılık kenarları (döngü denetimi için).</summary>
    public async Task<List<OperationJobDependency>> JobEdgesAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OperationJobDependency>().AsNoTracking().Where(d => d.OrderId == orderId).ToListAsync(cancellationToken);

    /// <summary>Siparişin işleri (izlenir: sipariş iptalinde açık işler aynı kaydetmede iptal edilir).</summary>
    public async Task<List<OperationJob>> JobsOfOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OperationJob>().Where(j => j.OrderId == orderId).OrderBy(j => j.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Açık (tamamlanmamış, iptal edilmemiş) işlerdeki sipariş kalemleri: bir kalem aynı anda yalnızca bir açık işte bulunur.</summary>
    public async Task<HashSet<Guid>> LinesInOpenJobsAsync(Guid orderId, CancellationToken cancellationToken) =>
        [.. await (from l in data.Set<OperationJobLine>().AsNoTracking()
                   join j in data.Set<OperationJob>().AsNoTracking() on l.JobId equals j.Id
                   where j.OrderId == orderId && j.Status != "Completed" && j.Status != "Cancelled"
                   select l.OrderLineId).ToListAsync(cancellationToken)];

    /// <summary>Atama beklerken belirlenen süreden uzun kalmış, takibi üretilmemiş işler (zamanlayıcı).</summary>
    public async Task<List<OperationJob>> UnassignedJobsSinceAsync(DateTimeOffset cutoff, int take, CancellationToken cancellationToken) =>
        await data.Set<OperationJob>().AsNoTracking()
            .Where(j => j.Status == "AwaitingAssignment" && j.UnassignedAlertAt == null && j.CreatedAt <= cutoff).OrderBy(j => j.CreatedAt).Take(take).ToListAsync(cancellationToken);

    /// <summary>Planlanan bitişi geçmiş, takibi üretilmemiş açık işler (zamanlayıcı).</summary>
    public async Task<List<OperationJob>> OverdueJobsAsync(DateOnly today, int take, CancellationToken cancellationToken) =>
        await data.Set<OperationJob>().AsNoTracking()
            .Where(j => j.Status != "Completed" && j.Status != "Cancelled" && j.PlannedEndDate != null && j.PlannedEndDate < today && j.OverdueAlertAt == null)
            .OrderBy(j => j.PlannedEndDate).Take(take).ToListAsync(cancellationToken);

    public Task<OrderDelivery?> DeliveryAsync(Guid id, CancellationToken cancellationToken) =>
        data.Set<OrderDelivery>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<List<OrderDelivery>> DeliveriesOfOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OrderDelivery>().AsNoTracking().Where(d => d.OrderId == orderId).OrderBy(d => d.PlannedDate).ThenBy(d => d.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Teslimat satırları (izlenir: teslim kaydında sipariş kalemleriyle aynı kaydetmede güncellenir).</summary>
    public async Task<List<OrderDeliveryLine>> DeliveryLinesAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        await data.Set<OrderDeliveryLine>().Where(l => l.DeliveryId == deliveryId).ToListAsync(cancellationToken);

    public async Task<List<OrderDeliveryLine>> DeliveryLinesOfOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OrderDeliveryLine>().AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync(cancellationToken);

    public async Task<List<(Guid Id, string FileName, string ContentType, int SizeBytes, string Sha256, string? Description, DateTimeOffset At, Guid? UploadedBy)>> EvidenceListAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        (await data.Set<OrderDeliveryEvidence>().AsNoTracking().Where(e => e.DeliveryId == deliveryId).OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.Description, e.CreatedAt, e.UploadedBy }).ToListAsync(cancellationToken))
        .Select(e => (e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.Description, e.CreatedAt, e.UploadedBy)).ToList();

    public Task<OrderDeliveryEvidence?> EvidenceAsync(Guid deliveryId, Guid evidenceId, CancellationToken cancellationToken) =>
        data.Set<OrderDeliveryEvidence>().AsNoTracking().SingleOrDefaultAsync(e => e.DeliveryId == deliveryId && e.Id == evidenceId, cancellationToken);

    public Task<int> EvidenceCountAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        data.Set<OrderDeliveryEvidence>().AsNoTracking().CountAsync(e => e.DeliveryId == deliveryId, cancellationToken);

    public async Task<List<OrderServiceStart>> ServiceStartsOfOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OrderServiceStart>().AsNoTracking().Where(s => s.OrderId == orderId).ToListAsync(cancellationToken);

    public Task<OrderServiceStart?> ServiceStartAsync(Guid id, CancellationToken cancellationToken) =>
        data.Set<OrderServiceStart>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    /// <summary>Planlı teslimatlar (izlenir; sipariş iptalinde iptal edilir).</summary>
    public async Task<List<OrderDelivery>> PlannedDeliveriesTrackedAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<OrderDelivery>().Where(d => d.OrderId == orderId && d.Status == "Planned").ToListAsync(cancellationToken);

    /// <summary>Hizmet kaleminin bağlı olduğu açık iş (varsa).</summary>
    public async Task<OperationJob?> OpenJobOfLineAsync(Guid orderLineId, CancellationToken cancellationToken) =>
        await (from l in data.Set<OperationJobLine>().AsNoTracking()
               join j in data.Set<OperationJob>().AsNoTracking() on l.JobId equals j.Id
               where l.OrderLineId == orderLineId && j.Status != "Completed" && j.Status != "Cancelled"
               select j).FirstOrDefaultAsync(cancellationToken);

    public Task<SalesOrder?> OrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        data.Set<SalesOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
}
