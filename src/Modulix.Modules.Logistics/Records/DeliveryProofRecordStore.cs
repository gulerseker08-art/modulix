using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Teslim kanıtı deposu (ADR-0038 K8, dilim 6). Kayıt <b>değişmez</b>: güncellenmez ve silinmez, genel uçtan oluşturulamaz; yalnızca teslim komutu
/// (<see cref="Lifecycle.IShipmentDelivery"/>) iş akışı kapısını açıp yazar. Zaman sunucuda verilir. Kuralları depo da uygular, komut atlanamaz:
/// yalnızca yoldaki sevkiyata kanıt yazılır; teslim ve kısmi teslimde teslim alan, kısmi teslimde açıklama, başarısız teslimde neden zorunludur.
/// Fotoğraf ve imza dosyaları kayda Core dosya deposuyla eklenir (okuma = sevkiyat okuma izni, ekleme = sevkiyat yazma izni).
/// </summary>
internal sealed class DeliveryProofRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<DeliveryProof>(context)
{
    public override string EntityKey => LogisticsModule.Entities.DeliveryProof;

    protected override string ReadPermission => LogisticsModule.Permissions.ShipmentsRead;

    protected override string WritePermission => LogisticsModule.Permissions.ShipmentsWrite;

    protected override string? ImmutableMessage => "Teslim kanıtı değiştirilemez ve silinemez; düzeltme için yeni kanıt girin.";

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Teslim kanıtı yalnızca sevkiyatın teslim komutuyla oluşur.";

    protected override IReadOnlyList<RecordField<DeliveryProof>> Fields { get; } =
    [
        Field.Reference<DeliveryProof, Shipment>("shipmentId", "Sevkiyat bulunamadı.", p => p.ShipmentId, (p, v) => p.ShipmentId = v!.Value, required: true, createOnly: true),
        Field.Choice<DeliveryProof>("outcome", DeliveryProof.Outcomes.All, p => p.Outcome, (p, v) => p.Outcome = v, createOnly: true),
        Field.Text<DeliveryProof>("receivedBy", DeliveryProof.ReceivedByMaxLength, p => p.ReceivedBy, (p, v) => p.ReceivedBy = v),
        Field.Computed<DeliveryProof>("receivedAt", p => JsonValue.Create(p.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))),
        Field.Flag<DeliveryProof>("damaged", p => p.Damaged, (p, v) => p.Damaged = v),
        Field.Flag<DeliveryProof>("shortage", p => p.Shortage, (p, v) => p.Shortage = v),
        Field.Text<DeliveryProof>("failureReason", DeliveryProof.TextMaxLength, p => p.FailureReason, (p, v) => p.FailureReason = v),
        Field.Text<DeliveryProof>("notes", DeliveryProof.TextMaxLength, p => p.Notes, (p, v) => p.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<DeliveryProof>> Filters { get; } = new Dictionary<string, RecordFilter<DeliveryProof>>
    {
        ["shipmentId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(p => p.ShipmentId == id) : null,
        ["outcome"] = (q, v) => DeliveryProof.Outcomes.All.Contains(v) ? q.Where(p => p.Outcome == v) : null,
    };

    // Yeni kanıt önce: bir sevkiyatın son kanıtı geçerlidir.
    protected override IOrderedQueryable<DeliveryProof> Order(IQueryable<DeliveryProof> source) =>
        source.OrderByDescending(p => p.ReceivedAt);

    protected override IQueryable<DeliveryProof> Search(IQueryable<DeliveryProof> source, string term) =>
        source.Where(p => (p.ReceivedBy != null && p.ReceivedBy.Contains(term)) || (p.Notes != null && p.Notes.Contains(term)));

    protected override async Task AppliedAsync(DeliveryProof entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var status = await Context.Data.Set<Shipment>().AsNoTracking().Where(s => s.Id == entity.ShipmentId)
            .Select(s => s.Status).SingleAsync(cancellationToken);
        if (status != Shipment.Statuses.InTransit)
        {
            throw new RecordConflictException("Teslim kanıtı yalnızca yoldaki sevkiyata yazılır.");
        }

        entity.ReceivedAt = DateTimeOffset.UtcNow;
        entity.ReceivedBy = Trim(entity.ReceivedBy);
        entity.FailureReason = Trim(entity.FailureReason);
        entity.Notes = Trim(entity.Notes);

        var errors = new List<RecordFieldError>();
        if (entity.Outcome != DeliveryProof.Outcomes.Failed && entity.ReceivedBy is null)
        {
            errors.Add(new RecordFieldError("receivedBy", "Teslim alanın adı zorunludur."));
        }

        if (entity.Outcome == DeliveryProof.Outcomes.Partial && entity.Notes is null)
        {
            errors.Add(new RecordFieldError("notes", "Kısmi teslimde neyin teslim edilmediğini açıklayın."));
        }

        if (entity.Outcome == DeliveryProof.Outcomes.Failed)
        {
            if (entity.FailureReason is null)
            {
                errors.Add(new RecordFieldError("failureReason", "Başarısız teslimin nedeni zorunludur."));
            }

            if (entity.ReceivedBy is not null || entity.Damaged || entity.Shortage)
            {
                errors.Add(new RecordFieldError("outcome", "Teslim edilemeyen sevkiyatta teslim alan, hasar ya da eksik işaretlenmez."));
            }
        }
        else if (entity.FailureReason is not null)
        {
            errors.Add(new RecordFieldError("failureReason", "Başarısız neden yalnızca teslim edilemeyen sevkiyatta girilir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
