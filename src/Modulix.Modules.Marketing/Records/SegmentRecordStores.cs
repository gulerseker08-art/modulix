using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Segments;

namespace Modulix.Modules.Marketing.Records;

/// <summary>
/// Segment kayıt deposu (ADR-0025). Kod, hedef türü ve tür oluşturulduktan sonra değişmez; durum, yayın sürümü ve hesaplama özeti yalnızca iş
/// akışı servisiyle yazılır (genel PUT yok sayar). Taslak koşul yalnızca dinamik segmentte, yazım denetiminden geçerek kanonik biçimde saklanır
/// (alan/yetki denetimi önizleme ve yayında yapılır). Arşivlenen segment düzenlenemez; yayımlanmış ya da üyesi olan segment silinmez (arşivlenir).
/// </summary>
internal sealed partial class SegmentRecordStore(MarketingStoreContext context) : MarketingRecordStore<Segment>(context)
{
    private readonly Dictionary<Guid, (string Code, string Target, string Kind)> before = [];

    public override string EntityKey => MarketingModule.Entities.Segment;

    protected override string ReadPermission => MarketingModule.Permissions.SegmentsRead;

    protected override string WritePermission => MarketingModule.Permissions.SegmentsWrite;

    protected override IReadOnlyList<RecordField<Segment>> Fields { get; } =
    [
        Field.Text<Segment>("code", Segment.CodeMaxLength, s => s.Code, (s, v) => s.Code = v!.ToLowerInvariant(), required: true),
        Field.Text<Segment>("name", Segment.NameMaxLength, s => s.Name, (s, v) => s.Name = v!, required: true),
        Field.Text<Segment>("description", Segment.DescriptionMaxLength, s => s.Description, (s, v) => s.Description = v),
        Field.Choice<Segment>("target", SegmentTargets.All, s => s.Target, (s, v) => s.Target = v),
        Field.Choice<Segment>("kind", SegmentKinds.All, s => s.Kind, (s, v) => s.Kind = v),
        Field.Text<Segment>("purpose", Segment.PurposeMaxLength, s => s.Purpose, (s, v) => s.Purpose = v),
        Field.Owner<Segment>("ownerId", s => s.OwnerId, (s, v) => s.OwnerId = v),
        Field.Choice<Segment>("status", SegmentStatuses.All, s => s.Status, (s, v) => s.Status = v).AsSystem(),
        Field.Text<Segment>("draft", Segment.DraftMaxLength, s => s.Draft, (s, v) => s.Draft = v),
        Field.Integer<Segment>("publishedVersion", s => s.PublishedVersion, (s, v) => s.PublishedVersion = v ?? 0).AsSystem(),
        Field.Time<Segment>("lastComputedAt", s => s.LastComputedAt, (s, v) => s.LastComputedAt = v).AsSystem(),
        Field.Integer<Segment>("lastMemberCount", s => s.LastMemberCount, (s, v) => s.LastMemberCount = v).AsSystem(),
        Field.Integer<Segment>("lastComputeMs", s => s.LastComputeMs, (s, v) => s.LastComputeMs = v).AsSystem(),
        Field.Text<Segment>("lastError", Segment.ErrorMaxLength, s => s.LastError, (s, v) => s.LastError = v).AsSystem(),
        Field.Id<Segment>("createdBy", s => s.CreatedBy, (s, v) => s.CreatedBy = v).AsSystem(),
        Field.Id<Segment>("updatedBy", s => s.UpdatedBy, (s, v) => s.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Segment>> Filters { get; } = new Dictionary<string, RecordFilter<Segment>>
    {
        ["status"] = (q, v, _) => SegmentStatuses.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["target"] = (q, v, _) => SegmentTargets.All.Contains(v) ? q.Where(x => x.Target == v) : null,
        ["kind"] = (q, v, _) => SegmentKinds.All.Contains(v) ? q.Where(x => x.Kind == v) : null,
        ["ownerId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(x => x.OwnerId == id) : null,
        ["usable"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(x => (x.Status == SegmentStatuses.Active) == flag) : null,
    };

    protected override IOrderedQueryable<Segment> Order(IQueryable<Segment> source) => source.OrderBy(s => s.Name);

    protected override IQueryable<Segment> Search(IQueryable<Segment> source, string term) =>
        source.Where(s => s.Name.Contains(term) || s.Code.Contains(term));

    protected override void OnUpdating(Segment entity) => before[entity.Id] = (entity.Code, entity.Target, entity.Kind);

    protected override async Task AppliedAsync(Segment entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (!CodePattern().IsMatch(entity.Code))
        {
            errors.Add(new RecordFieldError("code", "Kod 2-40 karakter olmalı; küçük harf, rakam ve tire kullanılabilir (ör. tekstil-marmara)."));
        }

        if (!isNew && before.TryGetValue(entity.Id, out var old))
        {
            if (old.Code != entity.Code)
            {
                errors.Add(new RecordFieldError("code", "Kod oluşturulduktan sonra değiştirilemez."));
            }

            if (old.Target != entity.Target)
            {
                errors.Add(new RecordFieldError("target", "Hedef türü oluşturulduktan sonra değiştirilemez."));
            }

            if (old.Kind != entity.Kind)
            {
                errors.Add(new RecordFieldError("kind", "Segment türü (statik/dinamik) oluşturulduktan sonra değiştirilemez."));
            }
        }

        if (entity.Kind == SegmentKinds.Static && !string.IsNullOrEmpty(entity.Draft))
        {
            errors.Add(new RecordFieldError("draft", "Statik segmentin koşulu olmaz; üyeler elle seçilir."));
        }
        else if (!string.IsNullOrEmpty(entity.Draft) && values.ContainsKey("draft"))
        {
            var (condition, parseErrors) = SegmentConditions.Parse(entity.Draft);
            if (condition is null)
            {
                errors.AddRange(parseErrors.Select(e => new RecordFieldError("draft", e.Path.Length == 0 ? e.Message : $"{e.Path}: {e.Message}")));
            }
            else
            {
                entity.Draft = SegmentConditions.Serialize(condition);
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (!isNew && entity.Status == SegmentStatuses.Archived && !Context.Gate.IsOpen)
        {
            throw new RecordConflictException("Arşivlenen segment düzenlenemez; önce geri alın.");
        }

        if (await Context.Data.Set<Segment>().AsNoTracking().AnyAsync(s => s.Code == entity.Code && s.Id != entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Bu kodlu bir segment zaten var.");
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
        }
    }

    protected override async Task BeforeDeleteAsync(Segment entity, CancellationToken cancellationToken)
    {
        if (entity.PublishedVersion > 0
            || await Context.Data.Set<SegmentMember>().AnyAsync(m => m.SegmentId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Yayımlanmış ya da üyesi olan segment silinemez; arşivleyin.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Segment üyesi kayıt deposu (statik üye ya da dinamik segmentte dahil/hariç istisna). Görünürlük segmentin sorumlusuna bağlıdır. Hedef, çağıranın
/// kayıt boru hattı yetkisiyle okunabilmelidir (başka tenant'ın ya da erişemediği kayıt "bulunamadı" sayılır). Statik segmentte yalnızca dahil edilen
/// üye bulunur; arşivlenen segmente üye eklenemez.
/// </summary>
internal sealed class SegmentMemberRecordStore(MarketingStoreContext context) : MarketingRecordStore<SegmentMember>(context)
{
    private readonly Dictionary<Guid, (Guid SegmentId, Guid TargetId)> before = [];

    public override string EntityKey => MarketingModule.Entities.SegmentMember;

    protected override string ReadPermission => MarketingModule.Permissions.SegmentsRead;

    protected override string WritePermission => MarketingModule.Permissions.SegmentsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => MarketingModule.Entities.Segment;

    protected override IReadOnlyList<RecordField<SegmentMember>> Fields { get; } =
    [
        Field.Reference<SegmentMember, Segment>("segmentId", "Segment bulunamadı.", m => m.SegmentId, (m, v) => m.SegmentId = v ?? Guid.Empty, required: true, createOnly: true),
        Field.Id<SegmentMember>("targetId", m => m.TargetId, (m, v) => m.TargetId = v ?? Guid.Empty),
        Field.Choice<SegmentMember>("kind", MemberKinds.All, m => m.Kind, (m, v) => m.Kind = v),
        Field.Text<SegmentMember>("reason", SegmentMember.ReasonMaxLength, m => m.Reason, (m, v) => m.Reason = v),
        Field.Id<SegmentMember>("addedBy", m => m.AddedBy, (m, v) => m.AddedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SegmentMember>> Filters { get; } = new Dictionary<string, RecordFilter<SegmentMember>>
    {
        ["segmentId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.SegmentId == id) : null,
        ["kind"] = (q, v, _) => MemberKinds.All.Contains(v) ? q.Where(x => x.Kind == v) : null,
        ["targetId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.TargetId == id) : null,
    };

    protected override IOrderedQueryable<SegmentMember> Order(IQueryable<SegmentMember> source) => source.OrderByDescending(m => m.CreatedAt);

    protected override IQueryable<SegmentMember> Search(IQueryable<SegmentMember> source, string term) => source.Where(m => m.Reason != null && m.Reason.Contains(term));

    protected override IQueryable<SegmentMember> ScopeToOwners(IQueryable<SegmentMember> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        var segments = Context.Data.Set<Segment>().AsNoTracking();
        return source.Where(m => segments.Any(s => s.Id == m.SegmentId && ids.Contains(s.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("segmentId", out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var segmentId))
        {
            return false;
        }

        var ids = owners.Select(o => (Guid?)o).ToList();
        return await Context.Data.Set<Segment>().AsNoTracking().AnyAsync(s => s.Id == segmentId && ids.Contains(s.OwnerId), cancellationToken);
    }

    protected override void OnUpdating(SegmentMember entity) => before[entity.Id] = (entity.SegmentId, entity.TargetId);

    protected override async Task AppliedAsync(SegmentMember entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.TargetId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("targetId", "Bu alan zorunludur."));
        }

        if (!isNew && before.TryGetValue(entity.Id, out var old) && old.TargetId != entity.TargetId)
        {
            errors.Add(new RecordFieldError("targetId", "Hedef oluşturulduktan sonra değiştirilemez; üyeyi silip yeniden ekleyin."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        var segment = await Context.Data.Set<Segment>().AsNoTracking().SingleAsync(s => s.Id == entity.SegmentId, cancellationToken);
        if (segment.Status == SegmentStatuses.Archived)
        {
            throw new RecordConflictException("Arşivlenen segmentin üyeleri değiştirilemez.");
        }

        if (segment.Kind == SegmentKinds.Static && entity.Kind == MemberKinds.Exclude)
        {
            throw new RecordValidationException([new RecordFieldError("kind", "Statik segmentte yalnızca dahil edilen üye bulunur.")]);
        }

        if (isNew)
        {
            var key = segment.Target switch
            {
                SegmentTargets.Person => SharedEntities.Person,
                SegmentTargets.Lead => "crm.lead",
                _ => SharedEntities.Party,
            };

            // Hedef çağıranın yetkisi ve veri kapsamıyla okunur; başka tenant'ın ya da erişemediği kayıt aynı hatayı verir.
            if (await Context.ReadRecordAsync(key, entity.TargetId, cancellationToken) is null)
            {
                throw new RecordValidationException([new RecordFieldError("targetId", "Hedef kayıt bulunamadı ya da erişiminiz yok.")]);
            }

            if (await Context.Data.Set<SegmentMember>().AsNoTracking()
                    .AnyAsync(m => m.SegmentId == entity.SegmentId && m.TargetId == entity.TargetId && m.Id != entity.Id, cancellationToken))
            {
                throw new RecordConflictException("Bu kayıt segmentte zaten var.");
            }
        }

        if (isNew)
        {
            entity.AddedBy = Context.CurrentUserId;
        }
    }
}

/// <summary>
/// Yayımlanmış sürüm kayıt deposu: yalnızca yayım eylemi (kapı açıkken) sürüm ekler; sürüm değiştirilemez ve silinemez. Genel uçlara açılmaz.
/// </summary>
internal sealed class SegmentVersionRecordStore(MarketingStoreContext context) : MarketingRecordStore<SegmentVersion>(context)
{
    public override string EntityKey => MarketingModule.Entities.SegmentVersion;

    protected override string ReadPermission => MarketingModule.Permissions.SegmentsRead;

    protected override string WritePermission => MarketingModule.Permissions.SegmentsPublish;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => MarketingModule.Entities.Segment;

    protected override IReadOnlyList<RecordField<SegmentVersion>> Fields { get; } =
    [
        Field.Id<SegmentVersion>("segmentId", v => v.SegmentId, (v, id) => v.SegmentId = id ?? Guid.Empty).AsSystem(),
        Field.Integer<SegmentVersion>("versionNo", v => v.VersionNo, (v, n) => v.VersionNo = n ?? 0).AsSystem(),
        Field.Text<SegmentVersion>("definition", Segment.DraftMaxLength, v => v.Definition, (v, text) => v.Definition = text ?? string.Empty).AsSystem(),
        Field.Time<SegmentVersion>("publishedAt", v => v.PublishedAt, (v, time) => v.PublishedAt = time ?? DateTimeOffset.UtcNow).AsSystem(),
        Field.Id<SegmentVersion>("publishedBy", v => v.PublishedBy, (v, id) => v.PublishedBy = id).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SegmentVersion>> Filters { get; } = new Dictionary<string, RecordFilter<SegmentVersion>>
    {
        ["segmentId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.SegmentId == id) : null,
    };

    protected override IOrderedQueryable<SegmentVersion> Order(IQueryable<SegmentVersion> source) => source.OrderByDescending(v => v.VersionNo);

    protected override IQueryable<SegmentVersion> Search(IQueryable<SegmentVersion> source, string term) => source;

    protected override IQueryable<SegmentVersion> ScopeToOwners(IQueryable<SegmentVersion> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(o => (Guid?)o).ToList();
        var segments = Context.Data.Set<Segment>().AsNoTracking();
        return source.Where(v => segments.Any(s => s.Id == v.SegmentId && ids.Contains(s.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("segmentId", out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var segmentId))
        {
            return false;
        }

        var ids = owners.Select(o => (Guid?)o).ToList();
        return await Context.Data.Set<Segment>().AsNoTracking().AnyAsync(s => s.Id == segmentId && ids.Contains(s.OwnerId), cancellationToken);
    }

    protected override Task<JsonObject> PrepareNewAsync(SegmentVersion entity, JsonObject values, CancellationToken cancellationToken) =>
        Context.Gate.IsOpen
            ? Task.FromResult(values)
            : throw new RecordValidationException([new RecordFieldError("segmentId", "Sürüm yalnızca yayım eylemiyle oluşur.")]);

    protected override Task AppliedAsync(SegmentVersion entity, JsonObject values, bool isNew, CancellationToken cancellationToken) =>
        isNew ? Task.CompletedTask : throw new RecordConflictException("Yayımlanmış sürüm değiştirilemez.");

    protected override Task BeforeDeleteAsync(SegmentVersion entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Yayımlanmış sürüm silinemez.");
}
