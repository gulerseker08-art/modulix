using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Blog yazıları (ADR-0042 dilim 8). "Blog yönetimi" izni olan bütün yazıları (taslak dahil) görür ve yazar; diğer okur yalnızca yayındaki yazıları
/// görür, görmediği yazı "bulunamadı"dır (yazıya bağlı dosyalar da). İlk yayında yayın anı yazılır.
/// </summary>
internal sealed class BlogPostRecordStore(HrStoreContext context) : HrRecordStore<BlogPost>(context)
{
    public override string EntityKey => HrModule.Entities.BlogPost;

    protected override string ReadPermission => HrModule.Permissions.BlogRead;

    protected override string WritePermission => HrModule.Permissions.BlogManage;

    protected override IReadOnlyList<RecordField<BlogPost>> Fields { get; } =
    [
        Field.Text<BlogPost>("title", BlogPost.TitleMaxLength, p => p.Title, (p, v) => p.Title = v!, required: true),
        Field.Text<BlogPost>("summary", BlogPost.SummaryMaxLength, p => p.Summary, (p, v) => p.Summary = v),
        Field.Text<BlogPost>("body", BlogPost.BodyMaxLength, p => p.Body, (p, v) => p.Body = v!, required: true),
        Field.Flag<BlogPost>("isPublished", p => p.IsPublished, (p, v) => p.IsPublished = v),
        Field.Computed<BlogPost>("publishedAt", p => p.PublishedAt?.ToString("O")),
        Field.Computed<BlogPost>("authorUserId", p => p.AuthorUserId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<BlogPost>> Filters { get; } = new Dictionary<string, RecordFilter<BlogPost>>
    {
        ["isPublished"] = (q, v) => bool.TryParse(v, out var published) ? q.Where(p => p.IsPublished == published) : null,
    };

    protected override IOrderedQueryable<BlogPost> Order(IQueryable<BlogPost> source) =>
        source.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("publishedAt", "title", "createdAt");

    protected override IOrderedQueryable<BlogPost> Order(IQueryable<BlogPost> source, string? sort) => sort?.TrimStart('-') switch
    {
        "publishedAt" => By(source, p => p.PublishedAt, sort.StartsWith('-')).ThenByDescending(p => p.CreatedAt),
        "title" => By(source, p => p.Title, sort.StartsWith('-')).ThenByDescending(p => p.CreatedAt),
        "createdAt" => By(source, p => p.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<BlogPost> Search(IQueryable<BlogPost> source, string term) =>
        source.Where(p => p.Title.Contains(term) || (p.Summary != null && p.Summary.Contains(term)) || p.Body.Contains(term));

    protected override async Task<IQueryable<BlogPost>> RestrictAsync(IQueryable<BlogPost> source, CancellationToken cancellationToken) =>
        await Context.Access.HasPermissionAsync(HrModule.Permissions.BlogManage, cancellationToken) ? source : source.Where(p => p.IsPublished);

    protected override Task AppliedAsync(BlogPost entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            entity.AuthorUserId = Context.CurrentUserId;
        }

        if (entity.IsPublished)
        {
            entity.PublishedAt ??= DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Fotoğraf albümleri (ADR-0042 dilim 8). Fotoğraflar albüme bağlı ADR-0040 dosyalarıdır: okur yalnızca yayındaki albümü ve fotoğraflarını görür,
/// fotoğrafı yalnızca "galeri yönetimi" izni olan ekler ve siler (dosya yükleme kayıt türünün güncelleme iznini ister).
/// </summary>
internal sealed class PhotoAlbumRecordStore(HrStoreContext context) : HrRecordStore<PhotoAlbum>(context)
{
    public override string EntityKey => HrModule.Entities.PhotoAlbum;

    protected override string ReadPermission => HrModule.Permissions.GalleryRead;

    protected override string WritePermission => HrModule.Permissions.GalleryManage;

    protected override IReadOnlyList<RecordField<PhotoAlbum>> Fields { get; } =
    [
        Field.Text<PhotoAlbum>("title", PhotoAlbum.TitleMaxLength, a => a.Title, (a, v) => a.Title = v!, required: true),
        Field.Text<PhotoAlbum>("description", PhotoAlbum.DescriptionMaxLength, a => a.Description, (a, v) => a.Description = v),
        Field.Date<PhotoAlbum>("eventDate", a => a.EventDate, (a, v) => a.EventDate = v),
        Field.Flag<PhotoAlbum>("isPublished", a => a.IsPublished, (a, v) => a.IsPublished = v),
        Field.Computed<PhotoAlbum>("publishedAt", a => a.PublishedAt?.ToString("O")),
        Field.Computed<PhotoAlbum>("authorUserId", a => a.AuthorUserId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<PhotoAlbum>> Filters { get; } = new Dictionary<string, RecordFilter<PhotoAlbum>>
    {
        ["isPublished"] = (q, v) => bool.TryParse(v, out var published) ? q.Where(a => a.IsPublished == published) : null,
    };

    protected override IOrderedQueryable<PhotoAlbum> Order(IQueryable<PhotoAlbum> source) =>
        source.OrderByDescending(a => a.EventDate).ThenByDescending(a => a.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("eventDate", "title", "createdAt");

    protected override IOrderedQueryable<PhotoAlbum> Order(IQueryable<PhotoAlbum> source, string? sort) => sort?.TrimStart('-') switch
    {
        "eventDate" => By(source, a => a.EventDate, sort.StartsWith('-')).ThenByDescending(a => a.CreatedAt),
        "title" => By(source, a => a.Title, sort.StartsWith('-')).ThenByDescending(a => a.CreatedAt),
        "createdAt" => By(source, a => a.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<PhotoAlbum> Search(IQueryable<PhotoAlbum> source, string term) =>
        source.Where(a => a.Title.Contains(term) || (a.Description != null && a.Description.Contains(term)));

    protected override async Task<IQueryable<PhotoAlbum>> RestrictAsync(IQueryable<PhotoAlbum> source, CancellationToken cancellationToken) =>
        await Context.Access.HasPermissionAsync(HrModule.Permissions.GalleryManage, cancellationToken) ? source : source.Where(a => a.IsPublished);

    protected override Task AppliedAsync(PhotoAlbum entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            entity.AuthorUserId = Context.CurrentUserId;
        }

        if (entity.IsPublished)
        {
            entity.PublishedAt ??= DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }
}
