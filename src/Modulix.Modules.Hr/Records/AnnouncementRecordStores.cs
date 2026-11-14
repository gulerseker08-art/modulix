using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Duyurular (ADR-0042 dilim 6). "Duyuru yönetimi" izni olan bütün duyuruları (taslak, ileri tarihli, süresi geçmiş) görür ve yazar. Diğer okur
/// yalnızca yayında, başlangıç günü gelmiş, süresi dolmamış ve kendisine hedeflenmiş (hedefi yok ya da hedeflerden biri okurun Core departmanı,
/// onun bir üst departmanı ya da okurun kendisi; bkz. <see cref="HrAudience"/>) duyuruları görür; görmediği duyuru "bulunamadı"dır. Bitiş günü başlangıçtan önce olamaz; başlangıç verilmezse bugündür. Silinen duyurunun okundu
/// kayıtları ve hedefleri de silinir.
/// </summary>
internal sealed class AnnouncementRecordStore(HrStoreContext context) : HrRecordStore<Announcement>(context)
{
    private readonly Dictionary<Guid, int> readCounts = [];
    private readonly HashSet<Guid> readByMe = [];
    private readonly AudienceFields<Announcement> audience =
        new(context, a => a.AudienceBusinessUnitId = null, a => a.AudienceDepartmentKey = null);

    public override string EntityKey => HrModule.Entities.Announcement;

    protected override string ReadPermission => HrModule.Permissions.AnnouncementsRead;

    protected override string WritePermission => HrModule.Permissions.AnnouncementsManage;

    protected override IReadOnlyList<RecordField<Announcement>> Fields => fields ??=
    [
        Field.Text<Announcement>("title", Announcement.TitleMaxLength, a => a.Title, (a, v) => a.Title = v!, required: true),
        Field.Text<Announcement>("body", Announcement.BodyMaxLength, a => a.Body, (a, v) => a.Body = v!, required: true),
        .. audience.Fields(),
        Field.Flag<Announcement>("isPinned", a => a.IsPinned, (a, v) => a.IsPinned = v),
        Field.Flag<Announcement>("isPublished", a => a.IsPublished, (a, v) => a.IsPublished = v),
        Field.Date<Announcement>("publishOn", a => a.PublishOn == default ? null : a.PublishOn, (a, v) => a.PublishOn = v ?? default),
        Field.Date<Announcement>("expiresOn", a => a.ExpiresOn, (a, v) => a.ExpiresOn = v),
        Field.Computed<Announcement>("authorUserId", a => a.AuthorUserId?.ToString()),
        Field.Computed<Announcement>("readCount", a => JsonValue.Create(readCounts.GetValueOrDefault(a.Id))),
        Field.Computed<Announcement>("readByMe", a => JsonValue.Create(readByMe.Contains(a.Id))),
    ];

    private IReadOnlyList<RecordField<Announcement>>? fields;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    protected override IReadOnlyDictionary<string, RecordFilter<Announcement>> Filters { get; } = new Dictionary<string, RecordFilter<Announcement>>
    {
        // Okurların gördüğü küme: yayında ve tarih aralığında.
        ["active"] = (q, v) => v switch
        {
            "true" => q.Where(a => a.IsPublished && a.PublishOn <= Today && (a.ExpiresOn == null || a.ExpiresOn >= Today)),
            "false" => q,
            _ => null,
        },
        ["isPublished"] = (q, v) => bool.TryParse(v, out var published) ? q.Where(a => a.IsPublished == published) : null,
        ["expired"] = (q, v) => v == "true" ? q.Where(a => a.ExpiresOn != null && a.ExpiresOn < Today) : null,

        // Kullanıcının henüz okumadığı duyurular (ana sayfa kutucuğundaki okunmamış sayısı).
        ["unread"] = (q, v) => v == "true" ? Unread(q, context) : null,
        ["audienceBusinessUnitId"] = (q, v) => FilterValue.Id(v) is { } id
            ? q.Where(a => a.AudienceBusinessUnitId == id
                || context.Data.Set<AudienceTarget>().Any(t => t.ContentId == a.Id && t.BusinessUnitId == id))
            : null,
        ["audienceDepartmentKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(a => a.AudienceDepartmentKey == v) : null,
    };

    private static IQueryable<Announcement> Unread(IQueryable<Announcement> source, HrStoreContext context)
    {
        var me = context.CurrentUserId ?? Guid.Empty;
        var reads = context.Data.Set<AnnouncementRead>().AsNoTracking();
        return source.Where(a => !reads.Any(r => r.AnnouncementId == a.Id && r.UserId == me));
    }

    protected override IOrderedQueryable<Announcement> Order(IQueryable<Announcement> source) =>
        source.OrderByDescending(a => a.IsPinned).ThenByDescending(a => a.PublishOn).ThenByDescending(a => a.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("publishOn", "title", "createdAt");

    protected override IOrderedQueryable<Announcement> Order(IQueryable<Announcement> source, string? sort) => sort?.TrimStart('-') switch
    {
        "publishOn" => By(source, a => a.PublishOn, sort.StartsWith('-')).ThenBy(a => a.CreatedAt),
        "title" => By(source, a => a.Title, sort.StartsWith('-')).ThenByDescending(a => a.PublishOn),
        "createdAt" => By(source, a => a.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<Announcement> Search(IQueryable<Announcement> source, string term) =>
        source.Where(a => a.Title.Contains(term) || a.Body.Contains(term));

    protected override async Task<IQueryable<Announcement>> RestrictAsync(IQueryable<Announcement> source, CancellationToken cancellationToken)
    {
        if (await Context.Access.HasPermissionAsync(HrModule.Permissions.AnnouncementsManage, cancellationToken))
        {
            return source;
        }

        var reader = await HrAudience.ReaderAsync(Context, cancellationToken);
        var today = Today;
        return HrAudience.Visible(source.Where(a => a.IsPublished && a.PublishOn <= today && (a.ExpiresOn == null || a.ExpiresOn >= today)),
            reader, Context.Data.Set<AudienceTarget>().AsNoTracking());
    }

    protected override async Task PrepareAsync(IReadOnlyCollection<Announcement> entities, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. entities.Select(a => a.Id)];
        var reads = Context.Data.Set<AnnouncementRead>().AsNoTracking().Where(r => ids.Contains(r.AnnouncementId));
        foreach (var row in await reads.GroupBy(r => r.AnnouncementId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        {
            readCounts[row.Key] = row.Count;
        }

        var me = Context.CurrentUserId ?? Guid.Empty;
        readByMe.UnionWith(await reads.Where(r => r.UserId == me).Select(r => r.AnnouncementId).ToListAsync(cancellationToken));
        await audience.PrepareAsync(entities, cancellationToken);
    }

    protected override async Task AppliedAsync(Announcement entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        // Hedefler (departmanlar ve/veya kişiler; boş: herkes) eski İK departman hedefinin yerini alır.
        await audience.AppliedAsync(entity, values, isNew, cancellationToken);

        if (entity.PublishOn == default)
        {
            entity.PublishOn = Today;
        }

        if (isNew)
        {
            entity.AuthorUserId = Context.CurrentUserId;
        }

        if (entity.ExpiresOn is { } expires && expires < entity.PublishOn)
        {
            throw new RecordValidationException([new RecordFieldError("expiresOn", "Bitiş günü başlangıç gününden önce olamaz.")]);
        }
    }

    protected override async Task BeforeDeleteAsync(Announcement entity, CancellationToken cancellationToken)
    {
        Context.Data.Set<AnnouncementRead>().RemoveRange(
            await Context.Data.Set<AnnouncementRead>().Where(r => r.AnnouncementId == entity.Id).ToListAsync(cancellationToken));
        await HrAudience.RemoveAsync(Context, entity.Id, cancellationToken);
    }
}

/// <summary>
/// Duyuru okundu kayıtları (ADR-0042 dilim 6). Yalnızca <see cref="AnnouncementService"/> yazar (okur duyuruyu açınca; kullanıcı başına tek kayıt);
/// değişmez, tek başına silinmez. Listesini yalnızca "duyuru yönetimi" izni olan görür (kim okudu).
/// </summary>
internal sealed class AnnouncementReadRecordStore(HrStoreContext context) : HrRecordStore<AnnouncementRead>(context)
{
    public override string EntityKey => HrModule.Entities.AnnouncementRead;

    protected override string ReadPermission => HrModule.Permissions.AnnouncementsManage;

    protected override string WritePermission => HrModule.Permissions.AnnouncementsManage;

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Okundu bilgisi duyuru açılınca yazılır.";

    protected override string? ImmutableMessage => "Okundu bilgisi değiştirilmez.";

    protected override IReadOnlyList<RecordField<AnnouncementRead>> Fields { get; } =
    [
        Field.Reference<AnnouncementRead, Announcement>("announcementId", "Duyuru bulunamadı.", r => r.AnnouncementId, (r, v) => r.AnnouncementId = v!.Value, required: true, createOnly: true),
        Field.Computed<AnnouncementRead>("userId", r => r.UserId.ToString()),
        Field.Computed<AnnouncementRead>("readAt", r => r.ReadAt.ToString("O")),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<AnnouncementRead>> Filters { get; } = new Dictionary<string, RecordFilter<AnnouncementRead>>
    {
        ["announcementId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(r => r.AnnouncementId == id) : null,
    };

    protected override IOrderedQueryable<AnnouncementRead> Order(IQueryable<AnnouncementRead> source) => source.OrderByDescending(r => r.ReadAt);

    protected override IQueryable<AnnouncementRead> Search(IQueryable<AnnouncementRead> source, string term) => source;

    protected override Task AppliedAsync(AnnouncementRead entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        entity.UserId = Context.CurrentUserId ?? throw new RecordConflictException("Aktif kullanıcı yok.");
        entity.ReadAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}

/// <summary>Duyuruyu okundu işaretler (ADR-0042 dilim 6).</summary>
public interface IAnnouncementService
{
    /// <summary>
    /// Kullanıcı duyuruyu görebiliyorsa (yoksa 404) okundu kaydı yazılır; daha önce okunduysa yeniden yazılmaz. Duyurunun güncel hali döner.
    /// </summary>
    Task<RecordResult<RecordData>> MarkReadAsync(Guid announcementId, CancellationToken cancellationToken);
}

internal sealed class AnnouncementService(IRecordPipeline pipeline, HrStoreContext context) : IAnnouncementService
{
    public async Task<RecordResult<RecordData>> MarkReadAsync(Guid announcementId, CancellationToken cancellationToken)
    {
        var announcement = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Announcement, announcementId), cancellationToken);
        if (!announcement.IsSuccess || announcement.Value!.Values["readByMe"]?.GetValue<bool>() == true)
        {
            return announcement;
        }

        RecordResult<RecordData> read;
        using (context.Gate.Open(HrModule.Permissions.AnnouncementsRead, "read", reason: null))
        {
            read = await pipeline.CreateAsync(new RecordCommand(HrModule.Entities.AnnouncementRead, null, new JsonObject
            {
                ["announcementId"] = announcementId.ToString(),
            }), cancellationToken);
        }

        return read.IsSuccess
            ? await pipeline.GetAsync(new RecordRef(HrModule.Entities.Announcement, announcementId), cancellationToken)
            : RecordResult<RecordData>.Fail(read.Error!.Code, read.Error.Message, read.Error.FieldErrors);
    }
}
