using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Records;

/// <summary>
/// Kampanya yanıtı kayıt deposu (ADR-0025, dilim 7). Bütün alanlar yalnızca yanıt servisinin eylemleriyle (kapı açıkken) yazılır: kampanya, çalıştırma, teslimat ve hedef
/// kimlikleri gönderim kaydından kopyalanır, istemci veremez; durum yalnızca eylemle değişir. Görünürlük yanıtın sorumlusuna bağlıdır (veri kapsamı). Yanıt kaydı silinmez, kapatılır.
/// </summary>
internal sealed class CampaignResponseRecordStore(MarketingStoreContext context) : MarketingRecordStore<CampaignResponse>(context)
{
    public override string EntityKey => MarketingModule.Entities.CampaignResponse;

    protected override string ReadPermission => MarketingModule.Permissions.ResponsesRead;

    protected override string WritePermission => MarketingModule.Permissions.ResponsesWrite;

    protected override IReadOnlyList<RecordField<CampaignResponse>> Fields { get; } =
    [
        Field.Reference<CampaignResponse, Campaign>("campaignId", "Kampanya bulunamadı.", r => r.CampaignId, (r, v) => r.CampaignId = v ?? Guid.Empty, required: true, createOnly: true).AsSystem(),
        Field.Id<CampaignResponse>("runId", r => r.RunId, (r, v) => r.RunId = v).AsSystem(),
        Field.Id<CampaignResponse>("deliveryId", r => r.DeliveryId, (r, v) => r.DeliveryId = v ?? Guid.Empty).AsSystem(),
        Field.Id<CampaignResponse>("targetId", r => r.TargetId, (r, v) => r.TargetId = v ?? Guid.Empty).AsSystem(),
        Field.Id<CampaignResponse>("personId", r => r.PersonId, (r, v) => r.PersonId = v).AsSystem(),
        Field.Choice<CampaignResponse>("kind", ResponseKinds.All, r => r.Kind, (r, v) => r.Kind = v).AsSystem(),
        Field.Text<CampaignResponse>("note", CampaignResponse.NoteMaxLength, r => r.Note, (r, v) => r.Note = v).AsSystem(),
        Field.Time<CampaignResponse>("receivedAt", r => r.ReceivedAt, (r, v) => { if (v is { } at) { r.ReceivedAt = at; } }).AsSystem(),
        Field.Choice<CampaignResponse>("status", ResponseStatuses.All, r => r.Status, (r, v) => r.Status = v).AsSystem(),
        Field.Owner<CampaignResponse>("ownerId", r => r.OwnerId, (r, v) => r.OwnerId = v).AsSystem(),
        Field.Id<CampaignResponse>("leadId", r => r.LeadId, (r, v) => r.LeadId = v).AsSystem(),
        Field.Id<CampaignResponse>("activityId", r => r.ActivityId, (r, v) => r.ActivityId = v).AsSystem(),
        Field.Time<CampaignResponse>("optOutAppliedAt", r => r.OptOutAppliedAt, (r, v) => r.OptOutAppliedAt = v).AsSystem(),
        Field.Text<CampaignResponse>("closedReason", CampaignResponse.ReasonMaxLength, r => r.ClosedReason, (r, v) => r.ClosedReason = v).AsSystem(),
        Field.Id<CampaignResponse>("createdBy", r => r.CreatedBy, (r, v) => r.CreatedBy = v).AsSystem(),
        Field.Id<CampaignResponse>("updatedBy", r => r.UpdatedBy, (r, v) => r.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CampaignResponse>> Filters { get; } = new Dictionary<string, RecordFilter<CampaignResponse>>
    {
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CampaignId == id) : null,
        ["runId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.RunId == id) : null,
        ["deliveryId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.DeliveryId == id) : null,
        // Hedef (firma/kişi/lead) ya da o hedefteki kişi için yanıtlar; müşteri kartındaki pazarlama geçmişi kullanır.
        ["targetId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.TargetId == id || x.PersonId == id) : null,
        ["status"] = (q, v, _) => ResponseStatuses.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["kind"] = (q, v, _) => ResponseKinds.All.Contains(v) ? q.Where(x => x.Kind == v) : null,
        ["ownerId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(x => x.OwnerId == id) : null,
    };

    protected override IOrderedQueryable<CampaignResponse> Order(IQueryable<CampaignResponse> source) => source.OrderByDescending(r => r.ReceivedAt);

    protected override IQueryable<CampaignResponse> Search(IQueryable<CampaignResponse> source, string term) => source.Where(r => r.Note != null && r.Note.Contains(term));

    protected override Task AppliedAsync(CampaignResponse entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }

        return Task.CompletedTask;
    }

    protected override Task BeforeDeleteAsync(CampaignResponse entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Yanıt kaydı silinmez; kapatın.");
}
