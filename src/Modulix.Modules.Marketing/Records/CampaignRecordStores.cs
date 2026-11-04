using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Marketing.Audiences;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Responses;

namespace Modulix.Modules.Marketing.Records;

/// <summary>
/// Kampanya kayıt deposu (ADR-0025, dilim 4). Kod, hedef türü ve kanal oluşturulduktan sonra değişmez; durum yalnızca eylemle değişir. Kişi seçme
/// kuralı firma hedefine özgüdür; "rol" kuralı rol ister. Arşivlenen kampanya düzenlenemez; hedef kitlesi ya da dondurulmuş kitlesi olan kampanya silinmez.
/// </summary>
internal sealed partial class CampaignRecordStore(MarketingStoreContext context) : MarketingRecordStore<Campaign>(context)
{
    private static readonly string[] Channels = [nameof(CommunicationChannel.Email), nameof(CommunicationChannel.Phone), nameof(CommunicationChannel.Sms), nameof(CommunicationChannel.WhatsApp)];

    private static readonly string[] Rules = [.. Enum.GetNames<RecipientRule>()];

    private readonly Dictionary<Guid, (string Code, string Target, string Channel, string? Topic)> before = [];

    public override string EntityKey => MarketingModule.Entities.Campaign;

    protected override string ReadPermission => MarketingModule.Permissions.CampaignsRead;

    protected override string WritePermission => MarketingModule.Permissions.CampaignsWrite;

    protected override IReadOnlyList<RecordField<Campaign>> Fields { get; } =
    [
        Field.Text<Campaign>("code", Campaign.CodeMaxLength, c => c.Code, (c, v) => c.Code = v!.ToLowerInvariant(), required: true),
        Field.Text<Campaign>("name", Campaign.NameMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Text<Campaign>("description", Campaign.DescriptionMaxLength, c => c.Description, (c, v) => c.Description = v),
        Field.Text<Campaign>("purpose", Campaign.PurposeMaxLength, c => c.Purpose, (c, v) => c.Purpose = v),
        Field.Choice<Campaign>("target", SegmentTargets.All, c => c.Target, (c, v) => c.Target = v),
        Field.Choice<Campaign>("channel", Channels, c => c.Channel, (c, v) => c.Channel = v),
        Field.Choice<Campaign>("recipientRule", Rules, c => c.RecipientRule, (c, v) => c.RecipientRule = v),
        Field.Text<Campaign>("recipientRole", Campaign.RoleMaxLength, c => c.RecipientRole, (c, v) => c.RecipientRole = v),
        Field.Integer<Campaign>("maxPerTarget", c => c.MaxPerTarget, (c, v) => c.MaxPerTarget = v, max: 1000),
        Field.Date<Campaign>("startsOn", c => c.StartsOn, (c, v) => c.StartsOn = v),
        Field.Date<Campaign>("endsOn", c => c.EndsOn, (c, v) => c.EndsOn = v),
        Field.Text<Campaign>("utmSource", Campaign.UtmMaxLength, c => c.UtmSource, (c, v) => c.UtmSource = v),
        Field.Text<Campaign>("utmMedium", Campaign.UtmMaxLength, c => c.UtmMedium, (c, v) => c.UtmMedium = v),
        Field.Text<Campaign>("utmCampaign", Campaign.UtmMaxLength, c => c.UtmCampaign, (c, v) => c.UtmCampaign = v),
        Field.Text<Campaign>("topic", Campaign.TopicMaxLength, c => c.Topic, (c, v) => c.Topic = v),
        Field.Money<Campaign>("budget", c => c.Budget, (c, v) => c.Budget = v),
        Field.Money<Campaign>("actualCost", c => c.ActualCost, (c, v) => c.ActualCost = v),
        Field.Text<Campaign>("costCurrency", 3, c => c.CostCurrency, (c, v) => c.CostCurrency = (v ?? "TRY").ToUpperInvariant()),
        Field.Owner<Campaign>("ownerId", c => c.OwnerId, (c, v) => c.OwnerId = v),
        Field.Choice<Campaign>("status", CampaignStatuses.All, c => c.Status, (c, v) => c.Status = v).AsSystem(),
        Field.Id<Campaign>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
        Field.Id<Campaign>("updatedBy", c => c.UpdatedBy, (c, v) => c.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Campaign>> Filters { get; } = new Dictionary<string, RecordFilter<Campaign>>
    {
        ["status"] = (q, v, _) => CampaignStatuses.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["target"] = (q, v, _) => SegmentTargets.All.Contains(v) ? q.Where(x => x.Target == v) : null,
        ["channel"] = (q, v, _) => Channels.Contains(v) ? q.Where(x => x.Channel == v) : null,
        ["ownerId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(x => x.OwnerId == id) : null,
    };

    protected override IOrderedQueryable<Campaign> Order(IQueryable<Campaign> source) => source.OrderByDescending(c => c.CreatedAt);

    protected override IQueryable<Campaign> Search(IQueryable<Campaign> source, string term) =>
        source.Where(c => c.Name.Contains(term) || c.Code.Contains(term));

    protected override void OnUpdating(Campaign entity) => before[entity.Id] = (entity.Code, entity.Target, entity.Channel, entity.Topic);

    protected override async Task AppliedAsync(Campaign entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (!CodePattern().IsMatch(entity.Code))
        {
            errors.Add(new RecordFieldError("code", "Kod 2-40 karakter olmalı; küçük harf, rakam ve tire kullanılabilir (ör. antrepo-2026)."));
        }

        if (!isNew && before.TryGetValue(entity.Id, out var old))
        {
            if (old.Code != entity.Code) { errors.Add(new RecordFieldError("code", "Kod oluşturulduktan sonra değiştirilemez.")); }
            if (old.Target != entity.Target) { errors.Add(new RecordFieldError("target", "Hedef türü oluşturulduktan sonra değiştirilemez.")); }
            if (old.Channel != entity.Channel) { errors.Add(new RecordFieldError("channel", "Kanal oluşturulduktan sonra değiştirilemez.")); }
        }

        if (entity.RecipientRule == nameof(RecipientRule.Role))
        {
            if (string.IsNullOrWhiteSpace(entity.RecipientRole))
            {
                errors.Add(new RecordFieldError("recipientRole", "Rol kuralında aranan rol zorunludur."));
            }
        }
        else
        {
            entity.RecipientRole = null;
        }

        if (!CurrencyPattern().IsMatch(entity.CostCurrency))
        {
            errors.Add(new RecordFieldError("costCurrency", "Para birimi 3 harfli kod olmalıdır (ör. TRY)."));
        }

        if (entity.MaxPerTarget is < 1)
        {
            errors.Add(new RecordFieldError("maxPerTarget", "Hedef başına alıcı sınırı en az 1 olmalıdır (sınır yoksa boş bırakın)."));
        }

        if (entity.StartsOn is { } start && entity.EndsOn is { } end && end < start)
        {
            errors.Add(new RecordFieldError("endsOn", "Bitiş tarihi başlangıçtan önce olamaz."));
        }

        // Kaynak/UTM değerleri küçük harfe çevrilir ve lead'e olduğu gibi geçtiği için güvenli karakterlerle sınırlıdır (bağlantı parametresi gibi kullanılabilir).
        entity.UtmSource = NormalizeUtm("utmSource", entity.UtmSource, errors);
        entity.UtmMedium = NormalizeUtm("utmMedium", entity.UtmMedium, errors);
        entity.UtmCampaign = NormalizeUtm("utmCampaign", entity.UtmCampaign, errors);

        // Konu (dilim 14): çekirdekte tanımlı ve açık olmalıdır. Konusu sonradan kapanan kampanya, konuyu değiştirmediği sürece düzenlenebilir.
        entity.Topic = string.IsNullOrWhiteSpace(entity.Topic) ? null : entity.Topic.Trim();
        var previousTopic = !isNew && before.TryGetValue(entity.Id, out var prior) ? prior.Topic : null;
        if (entity.Topic is not null && entity.Topic != previousTopic && !await Context.Topics.IsActiveAsync(entity.Topic, cancellationToken))
        {
            errors.Add(new RecordFieldError("topic", "Konu tanımlı ve açık olmalıdır (Yönetim → İletişim tercihleri → Konular)."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (!isNew && entity.Status == CampaignStatuses.Archived && !Context.Gate.IsOpen)
        {
            throw new RecordConflictException("Arşivlenen kampanya düzenlenemez; önce geri alın.");
        }

        if (await Context.Data.Set<Campaign>().AsNoTracking().AnyAsync(c => c.Code == entity.Code && c.Id != entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Bu kodlu bir kampanya zaten var.");
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }
    }

    protected override async Task BeforeDeleteAsync(Campaign entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<CampaignAudienceEntry>().AnyAsync(e => e.CampaignId == entity.Id, cancellationToken)
            || await Context.Data.Set<AudienceSnapshot>().AnyAsync(s => s.CampaignId == entity.Id, cancellationToken)
            || await Context.Data.Set<CampaignResponse>().AnyAsync(r => r.CampaignId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Hedef kitlesi, dondurulmuş kitlesi ya da yanıtı olan kampanya silinemez; arşivleyin.");
        }
    }

    private static string? NormalizeUtm(string field, string? value, List<RecordFieldError> errors)
    {
        var text = UtmRules.Normalize(value, out var valid);
        if (!valid)
        {
            errors.Add(new RecordFieldError(field, "Kaynak/UTM değeri harf, rakam, nokta, alt çizgi, tire ya da ~ içerebilir (en fazla 80 karakter, boşluk yok)."));
        }

        return text;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Hedef kitle girdisi (segment ya da elle seçilen hedef; dahil/hariç). Görünürlük kampanyanın sorumlusuna bağlıdır. Segment aynı hedef türünde ve
/// çağıranın görebildiği bir segment olmalı; elle hedef çağıranın boru hattı yetkisiyle okunabilmelidir (başka tenant'ın ya da erişemediği kayıt "bulunamadı").
/// </summary>
internal sealed class CampaignAudienceRecordStore(MarketingStoreContext context) : MarketingRecordStore<CampaignAudienceEntry>(context)
{
    private readonly Dictionary<Guid, (Guid? Segment, Guid? Target)> before = [];

    public override string EntityKey => MarketingModule.Entities.CampaignAudience;

    protected override string ReadPermission => MarketingModule.Permissions.CampaignsRead;

    protected override string WritePermission => MarketingModule.Permissions.CampaignsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => MarketingModule.Entities.Campaign;

    protected override IReadOnlyList<RecordField<CampaignAudienceEntry>> Fields { get; } =
    [
        Field.Reference<CampaignAudienceEntry, Campaign>("campaignId", "Kampanya bulunamadı.", e => e.CampaignId, (e, v) => e.CampaignId = v ?? Guid.Empty, required: true, createOnly: true),
        Field.Choice<CampaignAudienceEntry>("mode", AudienceModes.All, e => e.Mode, (e, v) => e.Mode = v),
        Field.Id<CampaignAudienceEntry>("segmentId", e => e.SegmentId, (e, v) => e.SegmentId = v),
        Field.Id<CampaignAudienceEntry>("targetId", e => e.TargetId, (e, v) => e.TargetId = v),
        Field.Text<CampaignAudienceEntry>("reason", CampaignAudienceEntry.ReasonMaxLength, e => e.Reason, (e, v) => e.Reason = v),
        Field.Id<CampaignAudienceEntry>("addedBy", e => e.AddedBy, (e, v) => e.AddedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CampaignAudienceEntry>> Filters { get; } = new Dictionary<string, RecordFilter<CampaignAudienceEntry>>
    {
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CampaignId == id) : null,
        ["mode"] = (q, v, _) => AudienceModes.All.Contains(v) ? q.Where(x => x.Mode == v) : null,
    };

    protected override IOrderedQueryable<CampaignAudienceEntry> Order(IQueryable<CampaignAudienceEntry> source) => source.OrderBy(e => e.Mode).ThenBy(e => e.CreatedAt);

    protected override IQueryable<CampaignAudienceEntry> Search(IQueryable<CampaignAudienceEntry> source, string term) =>
        source.Where(e => e.Reason != null && e.Reason.Contains(term));

    protected override IQueryable<CampaignAudienceEntry> ScopeToOwners(IQueryable<CampaignAudienceEntry> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        var campaigns = Context.Data.Set<Campaign>().AsNoTracking();
        return source.Where(e => campaigns.Any(c => c.Id == e.CampaignId && ids.Contains(c.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("campaignId", out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var campaignId))
        {
            return false;
        }

        var ids = owners.Select(o => (Guid?)o).ToList();
        return await Context.Data.Set<Campaign>().AsNoTracking().AnyAsync(c => c.Id == campaignId && ids.Contains(c.OwnerId), cancellationToken);
    }

    protected override void OnUpdating(CampaignAudienceEntry entity) => before[entity.Id] = (entity.SegmentId, entity.TargetId);

    protected override async Task AppliedAsync(CampaignAudienceEntry entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if ((entity.SegmentId is null) == (entity.TargetId is null))
        {
            throw new RecordValidationException([new RecordFieldError("segmentId", "Segment ya da hedef kayıttan tam biri seçilmelidir.")]);
        }

        if (!isNew && before.TryGetValue(entity.Id, out var old) && (old.Segment != entity.SegmentId || old.Target != entity.TargetId))
        {
            throw new RecordValidationException([new RecordFieldError("segmentId", "Kaynak oluşturulduktan sonra değiştirilemez; kaydı silip yeniden ekleyin.")]);
        }

        var campaign = await Context.Data.Set<Campaign>().AsNoTracking().SingleAsync(c => c.Id == entity.CampaignId, cancellationToken);
        if (campaign.Status == CampaignStatuses.Archived)
        {
            throw new RecordConflictException("Arşivlenen kampanyanın hedef kitlesi değiştirilemez.");
        }

        if (isNew)
        {
            if (entity.SegmentId is { } segmentId)
            {
                var segment = await Context.ReadRecordAsync(MarketingModule.Entities.Segment, segmentId, cancellationToken);
                if (segment is null)
                {
                    throw new RecordValidationException([new RecordFieldError("segmentId", "Segment bulunamadı ya da erişiminiz yok.")]);
                }

                if (segment.Values["target"]?.GetValue<string>() != campaign.Target)
                {
                    throw new RecordValidationException([new RecordFieldError("segmentId", "Segmentin hedef türü kampanyanınkiyle aynı olmalıdır; karışık türler birleştirilmez.")]);
                }

                if (segment.Values["status"]?.GetValue<string>() == SegmentStatuses.Archived)
                {
                    throw new RecordValidationException([new RecordFieldError("segmentId", "Arşivlenen segment yeni kampanyada kullanılamaz.")]);
                }
            }
            else
            {
                var key = campaign.Target switch
                {
                    SegmentTargets.Person => SharedEntities.Person,
                    SegmentTargets.Lead => "crm.lead",
                    _ => SharedEntities.Party,
                };
                if (await Context.ReadRecordAsync(key, entity.TargetId!.Value, cancellationToken) is null)
                {
                    throw new RecordValidationException([new RecordFieldError("targetId", "Hedef kayıt bulunamadı ya da erişiminiz yok.")]);
                }
            }

            var segmentKey = entity.SegmentId;
            var targetKey = entity.TargetId;
            if (await Context.Data.Set<CampaignAudienceEntry>().AsNoTracking().AnyAsync(
                    e => e.CampaignId == entity.CampaignId && e.Id != entity.Id && (segmentKey != null ? e.SegmentId == segmentKey : e.TargetId == targetKey), cancellationToken))
            {
                throw new RecordConflictException("Bu kaynak kampanyanın hedef kitlesinde zaten var.");
            }

            entity.AddedBy = Context.CurrentUserId;
        }
    }
}

/// <summary>
/// Dondurulmuş hedef kitle kayıt deposu: yalnızca hedef kitle servisi (kapı açıkken) ekler; değiştirilemez, silinemez. Alıcı listesi (kimlikler) denetim
/// kaydına ve genel okumaya girmesin diye alan listesinde değildir; <see cref="SnapshotPayload"/> ile eklenir ve <see cref="SnapshotRecipients"/> ile okunur.
/// </summary>
internal sealed class AudienceSnapshotRecordStore(MarketingStoreContext context, SnapshotPayload payload) : MarketingRecordStore<AudienceSnapshot>(context)
{
    public override string EntityKey => MarketingModule.Entities.AudienceSnapshot;

    protected override string ReadPermission => MarketingModule.Permissions.CampaignsRead;

    protected override string WritePermission => MarketingModule.Permissions.CampaignsAudience;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => MarketingModule.Entities.Campaign;

    protected override IReadOnlyList<RecordField<AudienceSnapshot>> Fields { get; } =
    [
        Field.Id<AudienceSnapshot>("campaignId", s => s.CampaignId, (s, v) => s.CampaignId = v ?? Guid.Empty).AsSystem(),
        Field.Text<AudienceSnapshot>("target", 20, s => s.Target, (s, v) => s.Target = v ?? string.Empty).AsSystem(),
        Field.Text<AudienceSnapshot>("channel", 20, s => s.Channel, (s, v) => s.Channel = v ?? string.Empty).AsSystem(),
        Field.Time<AudienceSnapshot>("takenAt", s => s.TakenAt, (s, v) => s.TakenAt = v ?? DateTimeOffset.UtcNow).AsSystem(),
        Field.Id<AudienceSnapshot>("takenBy", s => s.TakenBy, (s, v) => s.TakenBy = v).AsSystem(),
        Field.Integer<AudienceSnapshot>("targetCount", s => s.TargetCount, (s, v) => s.TargetCount = v ?? 0).AsSystem(),
        Field.Integer<AudienceSnapshot>("resolvedTargetCount", s => s.ResolvedTargetCount, (s, v) => s.ResolvedTargetCount = v ?? 0).AsSystem(),
        Field.Integer<AudienceSnapshot>("personCount", s => s.PersonCount, (s, v) => s.PersonCount = v ?? 0).AsSystem(),
        Field.Integer<AudienceSnapshot>("uniqueAddressCount", s => s.UniqueAddressCount, (s, v) => s.UniqueAddressCount = v ?? 0).AsSystem(),
        Field.Integer<AudienceSnapshot>("eligibleCount", s => s.EligibleCount, (s, v) => s.EligibleCount = v ?? 0).AsSystem(),
        Field.Integer<AudienceSnapshot>("excludedCount", s => s.ExcludedCount, (s, v) => s.ExcludedCount = v ?? 0).AsSystem(),
        Field.Text<AudienceSnapshot>("exclusions", 4000, s => s.Exclusions, (s, v) => s.Exclusions = v ?? "{}").AsSystem(),
        Field.Text<AudienceSnapshot>("unresolved", 4000, s => s.Unresolved, (s, v) => s.Unresolved = v ?? "{}").AsSystem(),
        Field.Text<AudienceSnapshot>("sources", 20000, s => s.Sources, (s, v) => s.Sources = v ?? "[]").AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<AudienceSnapshot>> Filters { get; } = new Dictionary<string, RecordFilter<AudienceSnapshot>>
    {
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CampaignId == id) : null,
    };

    protected override IOrderedQueryable<AudienceSnapshot> Order(IQueryable<AudienceSnapshot> source) => source.OrderByDescending(s => s.TakenAt);

    protected override IQueryable<AudienceSnapshot> Search(IQueryable<AudienceSnapshot> source, string term) => source;

    protected override IQueryable<AudienceSnapshot> ScopeToOwners(IQueryable<AudienceSnapshot> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        var campaigns = Context.Data.Set<Campaign>().AsNoTracking();
        return source.Where(s => campaigns.Any(c => c.Id == s.CampaignId && ids.Contains(c.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("campaignId", out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var campaignId))
        {
            return false;
        }

        var ids = owners.Select(o => (Guid?)o).ToList();
        return await Context.Data.Set<Campaign>().AsNoTracking().AnyAsync(c => c.Id == campaignId && ids.Contains(c.OwnerId), cancellationToken);
    }

    protected override Task<JsonObject> PrepareNewAsync(AudienceSnapshot entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen || payload.Recipients is null)
        {
            throw new RecordValidationException([new RecordFieldError("campaignId", "Dondurulmuş hedef kitle yalnızca hedef kitle eylemiyle oluşur.")]);
        }

        entity.Recipients = payload.Recipients;
        return Task.FromResult(values);
    }

    protected override Task AppliedAsync(AudienceSnapshot entity, JsonObject values, bool isNew, CancellationToken cancellationToken) =>
        isNew ? Task.CompletedTask : throw new RecordConflictException("Dondurulmuş hedef kitle değiştirilemez.");

    protected override Task BeforeDeleteAsync(AudienceSnapshot entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Dondurulmuş hedef kitle silinemez.");
}

/// <summary>Hedef kitle servisinin, dondurulacak alıcı listesini (JSON) kayıt deposuna ilettiği istek ömürlü taşıyıcı (alıcı listesi denetim kaydına girmez).</summary>
internal sealed class SnapshotPayload
{
    public string? Recipients { get; set; }
}

/// <summary>Dondurulmuş alıcı listesini okur. Çağıran önce kampanyayı kayıt boru hattından okumuş (izin ve veri kapsamı) olmalıdır.</summary>
internal sealed class SnapshotRecipients(MarketingStoreContext context)
{
    public async Task<string?> ReadAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        await context.Data.Set<AudienceSnapshot>().AsNoTracking().Where(s => s.Id == snapshotId).Select(s => s.Recipients).SingleOrDefaultAsync(cancellationToken);

    /// <summary>Hedef adları (firma: ünvan, kişi: ad soyad); lead için ad yoktur. Çağıranın ortak kaydı okuma yetkisi yoksa boş döner.</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> LabelsAsync(SegmentTarget target, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var key = target switch { SegmentTarget.Person => SharedEntities.Person, SegmentTarget.Lead => "crm.lead", _ => SharedEntities.Party };
        if (ids.Count == 0 || !await context.Access.CanAsync(key, RecordOperation.Read, cancellationToken))
        {
            return new Dictionary<Guid, string?>();
        }

        switch (target)
        {
            case SegmentTarget.Company:
                return (await context.Data.Set<Party>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken))
                    .ToDictionary(p => p.Id, p => (string?)p.Name);
            case SegmentTarget.Person:
                return (await context.Data.Set<Person>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.FirstName, p.LastName }).ToListAsync(cancellationToken))
                    .ToDictionary(p => p.Id, p => (string?)$"{p.FirstName} {p.LastName}".Trim());
            default:
                return new Dictionary<Guid, string?>();
        }
    }
}
