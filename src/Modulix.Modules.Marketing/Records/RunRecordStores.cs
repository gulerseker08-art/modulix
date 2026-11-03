using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Runs;

namespace Modulix.Modules.Marketing.Records;

/// <summary>
/// Kampanya içeriği kayıt deposu (ADR-0025, dilim 5): her kayıt yeni bir sürümdür; mevcut sürüm değiştirilemez ve silinemez. Sürüm numarasını depo atar.
/// İçerik kuralları (zorunlu alanlar, güvenli bağlantılar) kanalına göre doğrulanır.
/// </summary>
internal sealed class CampaignContentRecordStore(MarketingStoreContext context) : MarketingRecordStore<CampaignContent>(context)
{
    public override string EntityKey => MarketingModule.Entities.CampaignContent;

    protected override string ReadPermission => MarketingModule.Permissions.CampaignsRead;

    protected override string WritePermission => MarketingModule.Permissions.CampaignsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<CampaignContent>> Fields { get; } =
    [
        Field.Reference<CampaignContent, Campaign>("campaignId", "Kampanya bulunamadı.", c => c.CampaignId, (c, v) => c.CampaignId = v ?? Guid.Empty, required: true, createOnly: true),
        Field.Integer<CampaignContent>("versionNo", c => c.VersionNo, (c, v) => c.VersionNo = v ?? 0).AsSystem(),
        Field.Text<CampaignContent>("subject", CampaignContent.SubjectMaxLength, c => c.Subject, (c, v) => c.Subject = v),
        Field.Text<CampaignContent>("body", CampaignContent.BodyMaxLength, c => c.Body, (c, v) => c.Body = v ?? string.Empty, required: true),
        Field.Id<CampaignContent>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CampaignContent>> Filters { get; } = new Dictionary<string, RecordFilter<CampaignContent>>
    {
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CampaignId == id) : null,
    };

    protected override IOrderedQueryable<CampaignContent> Order(IQueryable<CampaignContent> source) => source.OrderByDescending(c => c.VersionNo);

    protected override IQueryable<CampaignContent> Search(IQueryable<CampaignContent> source, string term) =>
        source.Where(c => (c.Subject != null && c.Subject.Contains(term)) || c.Body.Contains(term));

    protected override IQueryable<CampaignContent> ScopeToOwners(IQueryable<CampaignContent> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        var campaigns = Context.Data.Set<Campaign>().AsNoTracking();
        return source.Where(c => campaigns.Any(k => k.Id == c.CampaignId && ids.Contains(k.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        CampaignScope.InScopeAsync(Context, record, owners, cancellationToken);

    protected override async Task<JsonObject> PrepareNewAsync(CampaignContent entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (values["campaignId"]?.GetValue<string>() is { } text && Guid.TryParse(text, out var campaignId))
        {
            entity.VersionNo = 1 + await Context.Data.Set<CampaignContent>().Where(c => c.CampaignId == campaignId).Select(c => (int?)c.VersionNo).MaxAsync(cancellationToken) ?? 1;
        }

        return values;
    }

    protected override async Task AppliedAsync(CampaignContent entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew)
        {
            throw new RecordConflictException("Yayımlanmış içerik sürümü değiştirilemez; yeni sürüm oluşturun.");
        }

        var campaign = await Context.Data.Set<Campaign>().AsNoTracking().SingleAsync(c => c.Id == entity.CampaignId, cancellationToken);
        if (campaign.Status == CampaignStatuses.Archived)
        {
            throw new RecordConflictException("Arşivlenen kampanyaya içerik eklenemez.");
        }

        var errors = ContentRules.Validate(Enum.Parse<CommunicationChannel>(campaign.Channel), entity.Subject, entity.Body);
        if (errors.Count > 0)
        {
            throw new RecordValidationException([.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);
        }

        entity.CreatedBy = Context.CurrentUserId;
    }

    protected override Task BeforeDeleteAsync(CampaignContent entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("İçerik sürümü silinemez.");
}

/// <summary>
/// Çalıştırma kayıt deposu (ADR-0025, dilim 5). Durum, onay ve zaman damgaları yalnızca eylem servisiyle (kapı açıkken) yazılır; içerik, anlık görüntü ve
/// planlanan zaman yalnızca taslakta düzenlenir ve kampanyaya ait olmalıdır. Taslak olmayan çalıştırma silinmez.
/// </summary>
internal sealed class CampaignRunRecordStore(MarketingStoreContext context) : MarketingRecordStore<CampaignRun>(context)
{
    public override string EntityKey => MarketingModule.Entities.CampaignRun;

    protected override string ReadPermission => MarketingModule.Permissions.CampaignsRead;

    protected override string WritePermission => MarketingModule.Permissions.CampaignsWrite;

    protected override IReadOnlyList<RecordField<CampaignRun>> Fields { get; } =
    [
        Field.Reference<CampaignRun, Campaign>("campaignId", "Kampanya bulunamadı.", r => r.CampaignId, (r, v) => r.CampaignId = v ?? Guid.Empty, required: true, createOnly: true),
        Field.Id<CampaignRun>("snapshotId", r => r.SnapshotId, (r, v) => r.SnapshotId = v),
        Field.Id<CampaignRun>("contentId", r => r.ContentId, (r, v) => r.ContentId = v),
        Field.Time<CampaignRun>("scheduledAt", r => r.ScheduledAt, (r, v) => r.ScheduledAt = v),
        Field.Owner<CampaignRun>("ownerId", r => r.OwnerId, (r, v) => r.OwnerId = v),
        Field.Choice<CampaignRun>("status", RunStatuses.All, r => r.Status, (r, v) => r.Status = v).AsSystem(),
        Field.Text<CampaignRun>("note", CampaignRun.NoteMaxLength, r => r.Note, (r, v) => r.Note = v).AsSystem(),
        Field.Id<CampaignRun>("submittedBy", r => r.SubmittedBy, (r, v) => r.SubmittedBy = v).AsSystem(),
        Field.Time<CampaignRun>("submittedAt", r => r.SubmittedAt, (r, v) => r.SubmittedAt = v).AsSystem(),
        Field.Id<CampaignRun>("approvedBy", r => r.ApprovedBy, (r, v) => r.ApprovedBy = v).AsSystem(),
        Field.Time<CampaignRun>("approvedAt", r => r.ApprovedAt, (r, v) => r.ApprovedAt = v).AsSystem(),
        Field.Id<CampaignRun>("approvedSnapshotId", r => r.ApprovedSnapshotId, (r, v) => r.ApprovedSnapshotId = v).AsSystem(),
        Field.Id<CampaignRun>("approvedContentId", r => r.ApprovedContentId, (r, v) => r.ApprovedContentId = v).AsSystem(),
        Field.Time<CampaignRun>("startedAt", r => r.StartedAt, (r, v) => r.StartedAt = v).AsSystem(),
        Field.Time<CampaignRun>("completedAt", r => r.CompletedAt, (r, v) => r.CompletedAt = v).AsSystem(),
        Field.Id<CampaignRun>("createdBy", r => r.CreatedBy, (r, v) => r.CreatedBy = v).AsSystem(),
        Field.Id<CampaignRun>("updatedBy", r => r.UpdatedBy, (r, v) => r.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CampaignRun>> Filters { get; } = new Dictionary<string, RecordFilter<CampaignRun>>
    {
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CampaignId == id) : null,
        ["status"] = (q, v, _) => RunStatuses.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["ownerId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(x => x.OwnerId == id) : null,
    };

    protected override IOrderedQueryable<CampaignRun> Order(IQueryable<CampaignRun> source) => source.OrderByDescending(r => r.CreatedAt);

    protected override IQueryable<CampaignRun> Search(IQueryable<CampaignRun> source, string term) => source.Where(r => r.Note != null && r.Note.Contains(term));

    protected override async Task AppliedAsync(CampaignRun entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew && !Context.Gate.IsOpen && !RunTransitions.IsEditable(entity.Status))
        {
            throw new RecordConflictException("Onaya gönderilmiş çalıştırma düzenlenemez; önce taslağa alın (onay yeniden alınır).");
        }

        if (Context.Gate.IsOpen)
        {
            entity.UpdatedBy = Context.CurrentUserId;
            if (isNew) { entity.CreatedBy = Context.CurrentUserId; }
            return;
        }

        var errors = new List<RecordFieldError>();
        var campaign = await Context.Data.Set<Campaign>().AsNoTracking().SingleAsync(c => c.Id == entity.CampaignId, cancellationToken);
        if (campaign.Status == CampaignStatuses.Archived)
        {
            throw new RecordConflictException("Arşivlenen kampanyada çalıştırma oluşturulamaz ya da düzenlenemez.");
        }

        if (entity.SnapshotId is { } snapshotId
            && !await Context.Data.Set<AudienceSnapshot>().AsNoTracking().AnyAsync(s => s.Id == snapshotId && s.CampaignId == entity.CampaignId, cancellationToken))
        {
            errors.Add(new RecordFieldError("snapshotId", "Hedef kitle anlık görüntüsü bu kampanyada bulunamadı."));
        }

        if (entity.ContentId is { } contentId
            && !await Context.Data.Set<CampaignContent>().AsNoTracking().AnyAsync(c => c.Id == contentId && c.CampaignId == entity.CampaignId, cancellationToken))
        {
            errors.Add(new RecordFieldError("contentId", "İçerik sürümü bu kampanyada bulunamadı."));
        }

        if (values.ContainsKey("scheduledAt") && entity.ScheduledAt is { } at && at <= Context.Time.GetUtcNow())
        {
            errors.Add(new RecordFieldError("scheduledAt", "Planlanan zaman geçmişte olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }
    }

    protected override IQueryable<CampaignRun> ScopeToOwners(IQueryable<CampaignRun> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        return source.Where(r => ids.Contains(r.OwnerId));
    }

    protected override Task BeforeDeleteAsync(CampaignRun entity, CancellationToken cancellationToken) =>
        entity.Status == RunStatuses.Draft ? Task.CompletedTask : throw new RecordConflictException("Yalnızca taslak çalıştırma silinir; diğerleri iptal edilir.");
}

/// <summary>Kapsam yardımcısı: kampanya kimliği taşıyan kayıt, kampanyanın sorumlusu veri kapsamındaysa görünür.</summary>
internal static class CampaignScope
{
    public static async Task<bool> InScopeAsync(MarketingStoreContext context, RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("campaignId", out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var campaignId))
        {
            return false;
        }

        var ids = owners.Select(o => (Guid?)o).ToList();
        return await context.Data.Set<Campaign>().AsNoTracking().AnyAsync(c => c.Id == campaignId && ids.Contains(c.OwnerId), cancellationToken);
    }
}

/// <summary>Teslimat günlüğü okuyucusu (kayıt deposu katmanı): sayımlar, sıradaki işler ve toplu durum değişikliği için izlenen satırlar.</summary>
internal sealed class DeliveryJournal(MarketingStoreContext context)
{
    private IQueryable<CampaignDelivery> For(Guid runId) => context.Data.Set<CampaignDelivery>().Where(d => d.RunId == runId);

    public async Task<IReadOnlyDictionary<string, int>> CountsAsync(Guid runId, CancellationToken cancellationToken) =>
        (await For(runId).AsNoTracking().GroupBy(d => d.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        .ToDictionary(g => g.Status, g => g.Count);

    public async Task<IReadOnlyDictionary<string, int>> CodesAsync(Guid runId, CancellationToken cancellationToken) =>
        (await For(runId).AsNoTracking().Where(d => d.Code != null).GroupBy(d => d.Status + ":" + d.Code).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        .ToDictionary(g => g.Key, g => g.Count);

    public async Task<(IReadOnlyList<CampaignDelivery> Items, int Total)> ListAsync(Guid runId, string? status, string? code, int page, int size, CancellationToken cancellationToken)
    {
        var query = For(runId).AsNoTracking();
        if (status is not null) { query = query.Where(d => d.Status == status); }
        if (code is not null) { query = query.Where(d => d.Code == code); }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>İzlenen satırlar (çağıran değiştirir ve günlük yazıcıyla kaydeder).</summary>
    public async Task<List<CampaignDelivery>> TrackedAsync(Guid runId, string status, int take, CancellationToken cancellationToken) =>
        await For(runId).Where(d => d.Status == status).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Take(take).ToListAsync(cancellationToken);

    public async Task<List<CampaignDelivery>> TrackedByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await context.Data.Set<CampaignDelivery>().Where(d => ids.Contains(d.Id)).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken);

    public Task<CampaignDelivery?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.Data.Set<CampaignDelivery>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<List<CampaignDelivery>> StaleSendingAsync(Guid runId, DateTimeOffset olderThan, CancellationToken cancellationToken) =>
        await For(runId).Where(d => d.Status == DeliveryStatuses.Sending && d.UpdatedAt < olderThan).ToListAsync(cancellationToken);

    public Task<bool> AnyAsync(Guid runId, string status, CancellationToken cancellationToken) =>
        For(runId).AsNoTracking().AnyAsync(d => d.Status == status, cancellationToken);

    public Task<bool> AnyOpenAsync(Guid runId, CancellationToken cancellationToken) =>
        For(runId).AsNoTracking().AnyAsync(d => d.Status == DeliveryStatuses.Queued || d.Status == DeliveryStatuses.Sending, cancellationToken);

    /// <summary>Çalışan çalıştırmalar (işçi için); en eski başlayan önce.</summary>
    public async Task<List<Guid>> RunningRunIdsAsync(int take, CancellationToken cancellationToken) =>
        await context.Data.Set<CampaignRun>().AsNoTracking().Where(r => r.Status == RunStatuses.Running).OrderBy(r => r.StartedAt).Select(r => r.Id).Take(take).ToListAsync(cancellationToken);

    public Task<bool> HasAnyAsync(Guid runId, CancellationToken cancellationToken) => For(runId).AsNoTracking().AnyAsync(cancellationToken);
}
