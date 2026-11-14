using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Yorum ve beğeni alan içerik türü (ADR-0042 bölüm 23d): duyuru ya da blog yazısı. Yorumu ve beğeniyi içeriği okuyabilen yazar (içeriğin okuma izni),
/// içeriği yöneten (yönetim izni) yorumu gizler ya da siler.
/// </summary>
internal sealed record ContentKind(string ContentEntity, string CommentEntity, string LikeEntity, string ReadPermission, string ManagePermission)
{
    public static ContentKind Announcement { get; } = new(HrModule.Entities.Announcement, HrModule.Entities.AnnouncementComment,
        HrModule.Entities.AnnouncementLike, HrModule.Permissions.AnnouncementsRead, HrModule.Permissions.AnnouncementsManage);

    public static ContentKind Blog { get; } = new(HrModule.Entities.BlogPost, HrModule.Entities.BlogComment,
        HrModule.Entities.BlogLike, HrModule.Permissions.BlogRead, HrModule.Permissions.BlogManage);

    public static ContentKind? Of(string entityKey) =>
        new[] { Announcement, Blog }.FirstOrDefault(k => k.ContentEntity == entityKey || k.CommentEntity == entityKey || k.LikeEntity == entityKey);

    /// <summary>Okurun görebildiği içeriklerin kimlikleri (içerik deposunun kuralıyla aynı: duyuruda yayın ve hedef kitle, blogda yayın).</summary>
    public async Task<IQueryable<Guid>> VisibleIdsAsync(HrStoreContext context, CancellationToken cancellationToken) =>
        this == Blog
            ? (await BlogPostRecordStore.ForReaderAsync(context, context.Data.Set<BlogPost>().AsNoTracking(), cancellationToken)).Select(p => p.Id)
            : (await AnnouncementRecordStore.ForReaderAsync(context, context.Data.Set<Announcement>().AsNoTracking(), cancellationToken)).Select(a => a.Id);
}

/// <summary>İçeriğin yorum ve beğeni sayıları ve kullanıcının beğenip beğenmediği (içerik listesinde gösterilir; gizli yorum sayılmaz).</summary>
internal sealed class ContentStats(HrStoreContext context)
{
    private readonly Dictionary<Guid, int> comments = [];
    private readonly Dictionary<Guid, int> likes = [];
    private readonly HashSet<Guid> likedByMe = [];

    public IEnumerable<RecordField<T>> Fields<T>() where T : HrEntity =>
    [
        Field.Computed<T>("commentCount", e => JsonValue.Create(comments.GetValueOrDefault(e.Id))),
        Field.Computed<T>("likeCount", e => JsonValue.Create(likes.GetValueOrDefault(e.Id))),
        Field.Computed<T>("likedByMe", e => JsonValue.Create(likedByMe.Contains(e.Id))),
    ];

    public async Task LoadAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];

        // Aynı istekte yeniden okunabilir (ör. beğeni aç/kapa sonrası): önceki değerler atılır.
        foreach (var id in wanted)
        {
            comments.Remove(id);
            likes.Remove(id);
            likedByMe.Remove(id);
        }

        foreach (var row in await context.Data.Set<ContentComment>().AsNoTracking().Where(c => wanted.Contains(c.ContentId) && !c.IsHidden)
            .GroupBy(c => c.ContentId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        {
            comments[row.Key] = row.Count;
        }

        var rows = context.Data.Set<ContentLike>().AsNoTracking().Where(l => wanted.Contains(l.ContentId));
        foreach (var row in await rows.GroupBy(l => l.ContentId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        {
            likes[row.Key] = row.Count;
        }

        var me = context.CurrentUserId ?? Guid.Empty;
        likedByMe.UnionWith(await rows.Where(l => l.UserId == me).Select(l => l.ContentId).ToListAsync(cancellationToken));
    }

    /// <summary>İçerik silinirken yorumları ve beğenileri de silinir.</summary>
    public static async Task RemoveAsync(HrStoreContext context, Guid contentId, CancellationToken cancellationToken)
    {
        context.Data.Set<ContentComment>().RemoveRange(await context.Data.Set<ContentComment>().Where(c => c.ContentId == contentId).ToListAsync(cancellationToken));
        context.Data.Set<ContentLike>().RemoveRange(await context.Data.Set<ContentLike>().Where(l => l.ContentId == contentId).ToListAsync(cancellationToken));
    }
}

/// <summary>
/// İçerik yorumları (ADR-0042 bölüm 23d). Okur yalnızca görebildiği içeriğin yorumlarını görür; gizlenmiş yorumun metni yalnızca içeriği yönetene
/// gösterilir. Yorum yalnızca <see cref="IContentInteractions"/> ile yazılır, gizlenir ve silinir; metni değiştirilmez.
/// </summary>
internal abstract class ContentCommentRecordStore<TContent>(HrStoreContext context, ContentKind kind) : HrRecordStore<ContentComment>(context)
    where TContent : HrEntity
{
    private bool canModerate;

    public override string EntityKey => kind.CommentEntity;

    protected override string ReadPermission => kind.ReadPermission;

    protected override string WritePermission => kind.ManagePermission;

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Yorum, yorum yazma eylemiyle eklenir.";

    protected override string? UpdateBlockedMessage => Context.Gate.IsOpen ? null : "Yorumun metni değiştirilmez; içeriği yöneten gizleyebilir.";

    protected override IReadOnlyList<RecordField<ContentComment>> Fields => fields ??=
    [
        Field.Reference<ContentComment, TContent>("contentId", "İçerik bulunamadı.", c => c.ContentId, (c, v) => c.ContentId = v!.Value, required: true, createOnly: true),
        Field.Text<ContentComment>("body", ContentComment.BodyMaxLength, c => c.IsHidden && !canModerate ? null : c.Body, (c, v) => c.Body = v!, required: true, createOnly: true),
        Field.Flag<ContentComment>("isHidden", c => c.IsHidden, (c, v) => c.IsHidden = v),
        Field.Computed<ContentComment>("authorUserId", c => c.AuthorUserId.ToString()),
        Field.Computed<ContentComment>("hiddenByUserId", c => c.HiddenByUserId?.ToString()),
        Field.Computed<ContentComment>("createdAt", c => c.CreatedAt.ToString("O")),
    ];

    private IReadOnlyList<RecordField<ContentComment>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<ContentComment>> Filters { get; } = new Dictionary<string, RecordFilter<ContentComment>>
    {
        ["contentId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.ContentId == id) : null,
    };

    protected override IOrderedQueryable<ContentComment> Order(IQueryable<ContentComment> source) => source.OrderBy(c => c.CreatedAt);

    protected override IQueryable<ContentComment> Search(IQueryable<ContentComment> source, string term) => source.Where(c => c.Body.Contains(term));

    protected override async Task<IQueryable<ContentComment>> RestrictAsync(IQueryable<ContentComment> source, CancellationToken cancellationToken)
    {
        var visible = await kind.VisibleIdsAsync(Context, cancellationToken);
        return source.Where(c => visible.Contains(c.ContentId));
    }

    protected override async Task PrepareAsync(IReadOnlyCollection<ContentComment> entities, CancellationToken cancellationToken) =>
        canModerate = await Context.Access.HasPermissionAsync(kind.ManagePermission, cancellationToken);

    protected override Task AppliedAsync(ContentComment entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            entity.AuthorUserId = Context.CurrentUserId ?? throw new RecordConflictException("Aktif kullanıcı yok.");
        }

        if (values.ContainsKey("isHidden"))
        {
            entity.HiddenByUserId = entity.IsHidden ? Context.CurrentUserId : null;
        }

        return Task.CompletedTask;
    }

    protected override Task BeforeDeleteAsync(ContentComment entity, CancellationToken cancellationToken) =>
        Context.Gate.IsOpen ? Task.CompletedTask : throw new RecordConflictException("Yorum, yorum silme eylemiyle silinir.");
}

internal sealed class AnnouncementCommentRecordStore(HrStoreContext context)
    : ContentCommentRecordStore<Announcement>(context, ContentKind.Announcement);

internal sealed class BlogCommentRecordStore(HrStoreContext context)
    : ContentCommentRecordStore<BlogPost>(context, ContentKind.Blog);

/// <summary>
/// İçerik beğenileri (ADR-0042 bölüm 23d): içerik başına kullanıcı tekil. Okur görebildiği içeriğin beğenilerini görür; beğeni yalnızca
/// <see cref="IContentInteractions"/> ile eklenir ve kaldırılır (aç/kapa).
/// </summary>
internal abstract class ContentLikeRecordStore<TContent>(HrStoreContext context, ContentKind kind) : HrRecordStore<ContentLike>(context)
    where TContent : HrEntity
{
    public override string EntityKey => kind.LikeEntity;

    protected override string ReadPermission => kind.ReadPermission;

    protected override string WritePermission => kind.ManagePermission;

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Beğeni, beğen eylemiyle eklenir.";

    protected override string? UpdateBlockedMessage => "Beğeni değiştirilmez.";

    protected override IReadOnlyList<RecordField<ContentLike>> Fields => fields ??=
    [
        Field.Reference<ContentLike, TContent>("contentId", "İçerik bulunamadı.", l => l.ContentId, (l, v) => l.ContentId = v!.Value, required: true, createOnly: true),
        Field.Computed<ContentLike>("userId", l => l.UserId.ToString()),
    ];

    private IReadOnlyList<RecordField<ContentLike>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<ContentLike>> Filters { get; } = new Dictionary<string, RecordFilter<ContentLike>>
    {
        ["contentId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.ContentId == id) : null,
    };

    protected override IOrderedQueryable<ContentLike> Order(IQueryable<ContentLike> source) => source.OrderBy(l => l.CreatedAt);

    protected override IQueryable<ContentLike> Search(IQueryable<ContentLike> source, string term) => source;

    protected override async Task<IQueryable<ContentLike>> RestrictAsync(IQueryable<ContentLike> source, CancellationToken cancellationToken)
    {
        var visible = await kind.VisibleIdsAsync(Context, cancellationToken);
        return source.Where(l => visible.Contains(l.ContentId));
    }

    protected override async Task AppliedAsync(ContentLike entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        entity.UserId = Context.CurrentUserId ?? throw new RecordConflictException("Aktif kullanıcı yok.");
        if (isNew && await Context.Data.Set<ContentLike>().AsNoTracking().AnyAsync(l => l.ContentId == entity.ContentId && l.UserId == entity.UserId, cancellationToken))
        {
            throw new RecordConflictException("Bu içeriği zaten beğendiniz.");
        }
    }

    protected override Task BeforeDeleteAsync(ContentLike entity, CancellationToken cancellationToken) =>
        Context.Gate.IsOpen ? Task.CompletedTask : throw new RecordConflictException("Beğeni, beğen eylemiyle kaldırılır.");
}

internal sealed class AnnouncementLikeRecordStore(HrStoreContext context)
    : ContentLikeRecordStore<Announcement>(context, ContentKind.Announcement);

internal sealed class BlogLikeRecordStore(HrStoreContext context)
    : ContentLikeRecordStore<BlogPost>(context, ContentKind.Blog);

/// <summary>Duyuru ve blog yazısında yorum ve beğeni (ADR-0042 bölüm 23d). Bütün yazmalar kayıt boru hattından geçer (yetki, denetim kaydı, olay).</summary>
public interface IContentInteractions
{
    /// <summary>İçeriği görebilen (yoksa 404) kullanıcı yorum yazar. Yazılan yorum döner.</summary>
    Task<RecordResult<RecordData>> CommentAsync(string contentEntity, Guid contentId, string? body, CancellationToken cancellationToken);

    /// <summary>
    /// Yorum eylemi: <c>hide</c> ve <c>show</c> içeriği yönetene; <c>delete</c> yorumun yazarına ya da içeriği yönetene. Gizleme/gösterme yorumun
    /// güncel halini, silme boş döner.
    /// </summary>
    Task<RecordResult<RecordData?>> ModerateAsync(string commentEntity, Guid commentId, string action, long version, CancellationToken cancellationToken);

    /// <summary>Beğeniyi açar ya da kapatır (içeriği görebilen kullanıcı). İçeriğin güncel hali (sayılar) döner.</summary>
    Task<RecordResult<RecordData>> ToggleLikeAsync(string contentEntity, Guid contentId, CancellationToken cancellationToken);
}

internal sealed class ContentInteractions(IRecordPipeline pipeline, HrStoreContext context) : IContentInteractions
{
    public static IReadOnlyList<string> Actions { get; } = ["hide", "show", "delete"];

    public async Task<RecordResult<RecordData>> CommentAsync(string contentEntity, Guid contentId, string? body, CancellationToken cancellationToken)
    {
        if (ContentKind.Of(contentEntity) is not { } kind || kind.ContentEntity != contentEntity)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.NotFound, "İçerik bulunamadı.");
        }

        var content = await pipeline.GetAsync(new RecordRef(kind.ContentEntity, contentId), cancellationToken);
        if (!content.IsSuccess)
        {
            return content;
        }

        using (context.Gate.Open(kind.ReadPermission, "comment", reason: null))
        {
            return await pipeline.CreateAsync(new RecordCommand(kind.CommentEntity, null, new JsonObject
            {
                ["contentId"] = contentId.ToString(),
                ["body"] = body,
            }), cancellationToken);
        }
    }

    public async Task<RecordResult<RecordData?>> ModerateAsync(string commentEntity, Guid commentId, string action, long version, CancellationToken cancellationToken)
    {
        if (ContentKind.Of(commentEntity) is not { } kind || kind.CommentEntity != commentEntity)
        {
            return RecordResult<RecordData?>.Fail(RecordErrorCode.NotFound, "Yorum bulunamadı.");
        }

        if (!Actions.Contains(action))
        {
            return RecordResult<RecordData?>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.",
                [new RecordFieldError("action", "Eylem hide, show ya da delete olmalıdır.")]);
        }

        var comment = await pipeline.GetAsync(new RecordRef(kind.CommentEntity, commentId), cancellationToken);
        if (!comment.IsSuccess)
        {
            return RecordResult<RecordData?>.Fail(comment.Error!.Code, comment.Error.Message, comment.Error.FieldErrors);
        }

        var moderator = await context.Access.HasPermissionAsync(kind.ManagePermission, cancellationToken);
        var author = comment.Value!.Values["authorUserId"]?.GetValue<string>() == context.CurrentUserId?.ToString();
        if (!moderator && !(action == "delete" && author))
        {
            return RecordResult<RecordData?>.Fail(RecordErrorCode.PermissionDenied,
                action == "delete" ? "Yalnızca kendi yorumunuzu silebilirsiniz." : "Yorumu gizlemek için içeriği yönetme yetkisi gerekir.");
        }

        using (context.Gate.Open(moderator ? kind.ManagePermission : kind.ReadPermission, action, reason: null))
        {
            if (action == "delete")
            {
                var deleted = await pipeline.DeleteAsync(new RecordRef(kind.CommentEntity, commentId), version, cancellationToken);
                return deleted.IsSuccess
                    ? RecordResult<RecordData?>.Ok(null)
                    : RecordResult<RecordData?>.Fail(deleted.Error!.Code, deleted.Error.Message, deleted.Error.FieldErrors);
            }

            var updated = await pipeline.UpdateAsync(
                new RecordCommand(kind.CommentEntity, commentId, new JsonObject { ["isHidden"] = action == "hide" }, version), cancellationToken);
            return updated.IsSuccess
                ? RecordResult<RecordData?>.Ok(updated.Value)
                : RecordResult<RecordData?>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
        }
    }

    public async Task<RecordResult<RecordData>> ToggleLikeAsync(string contentEntity, Guid contentId, CancellationToken cancellationToken)
    {
        if (ContentKind.Of(contentEntity) is not { } kind || kind.ContentEntity != contentEntity)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.NotFound, "İçerik bulunamadı.");
        }

        var content = await pipeline.GetAsync(new RecordRef(kind.ContentEntity, contentId), cancellationToken);
        if (!content.IsSuccess)
        {
            return content;
        }

        var me = context.CurrentUserId ?? Guid.Empty;
        var existing = await context.Data.Set<ContentLike>().AsNoTracking().Where(l => l.ContentId == contentId && l.UserId == me)
            .Select(l => new { l.Id, l.Version }).FirstOrDefaultAsync(cancellationToken);
        using (context.Gate.Open(kind.ReadPermission, "like", reason: null))
        {
            if (existing is null)
            {
                var liked = await pipeline.CreateAsync(new RecordCommand(kind.LikeEntity, null, new JsonObject { ["contentId"] = contentId.ToString() }), cancellationToken);
                if (!liked.IsSuccess)
                {
                    return liked;
                }
            }
            else
            {
                var removed = await pipeline.DeleteAsync(new RecordRef(kind.LikeEntity, existing.Id), existing.Version, cancellationToken);
                if (!removed.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(removed.Error!.Code, removed.Error.Message, removed.Error.FieldErrors);
                }
            }
        }

        return await pipeline.GetAsync(new RecordRef(kind.ContentEntity, contentId), cancellationToken);
    }
}
