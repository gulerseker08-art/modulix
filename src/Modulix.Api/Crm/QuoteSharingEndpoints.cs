using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Quotes;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Teklif belgesi (PDF), paylaşım bağlantısı ve müşteri sayfası uç noktaları (ADR-0019, dilim 3).
/// <list type="bullet">
///   <item><b>İç uçlar</b> (<c>/api/crm/quotes/{id}/documents|share-links|decisions</c>): oturumlu; belge okuma <c>crm.quotes.read</c>,
///   üretme/bağlantı <c>crm.quotes.share</c>. Maliyet ve iç not hiçbir çıktıya girmez.</item>
///   <item><b>Herkese açık uçlar</b> (<c>/api/public/quotes/{tenant}/view|pdf|decision</c>): oturumsuz; kimlik doğrulama bağlantı belirtecine
///   sahip olmaktır. Belirteç <b>yolda/sorguda değil gövdede</b> taşınır ve müşteri sayfasında URL parçasındadır (#): sunucu erişim
///   günlüklerine ve Referer başlığına girmez. Tenant URL'deki kısa addan sunucuda çözülür; istek "crm.quote-customer" sistem
///   kimliğiyle kendi tenant kapsamında çalışır. Hız sınırı (IP + tenant), 8 KB gövde sınırı, tek tip "geçersiz bağlantı" yanıtı ve
///   önbelleğe alma yasağı uygulanır.</item>
/// </list>
/// </summary>
internal static class QuoteSharingEndpoints
{
    public const string ViewPolicy = "quote-public";
    public const string DecisionPolicy = "quote-public-decision";
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>IP + tenant başına dakikadaki istek sınırı (<c>QuotePublic:RateLimitPerMinute</c>, varsayılan 60; karar için 1/6'sı).</summary>
    public static void AddQuotePublicRateLimit(this IServiceCollection services, IConfiguration configuration) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var permits = Math.Max(2, configuration.GetValue("QuotePublic:RateLimitPerMinute", 60));
            options.AddPolicy(ViewPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"view|{context.Connection.RemoteIpAddress}|{context.Request.RouteValues["tenant"]}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(DecisionPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"decision|{context.Connection.RemoteIpAddress}|{context.Request.RouteValues["tenant"]}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(2, permits / 6), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static IEndpointRouteBuilder MapQuotePublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/quotes/{tenant}").AllowAnonymous().WithTags("Teklif müşteri sayfası (herkese açık)");
        group.MapPost("/view", ViewAsync).RequireRateLimiting(ViewPolicy);
        group.MapPost("/pdf", PdfAsync).RequireRateLimiting(ViewPolicy);
        group.MapPost("/decision", DecisionAsync).RequireRateLimiting(DecisionPolicy);
        return app;
    }

    public static void MapInternal(RouteGroupBuilder quotes)
    {
        quotes.MapGet("/{id:guid}/documents", async (Guid id, IQuoteSharing sharing, CancellationToken ct) =>
        {
            var result = await sharing.ListDocumentsAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapGet("/{id:guid}/documents/{documentId:guid}/file", async (Guid id, Guid documentId, IQuoteSharing sharing, HttpContext http, CancellationToken ct) =>
        {
            var result = await sharing.GetDocumentAsync(id, documentId, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(result.Value!.Content, result.Value.ContentType, result.Value.Info.FileName);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        // Belgeyi sunulmuş tekliften üretir (yoksa); varsa mevcut belge döner. Belge değiştirilmez.
        quotes.MapPost("/{id:guid}/documents", async (Guid id, QuoteDocumentService documents, CancellationToken ct) =>
        {
            var result = await documents.EnsureAsync(id, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapGet("/{id:guid}/share-links", async (Guid id, IQuoteSharing sharing, CancellationToken ct) =>
        {
            var result = await sharing.ListLinksAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapPost("/{id:guid}/share-links", async (Guid id, CreateQuoteShareLinkRequest request, QuoteDocumentService documents, IQuoteSharing sharing,
            IServiceProvider services, CancellationToken ct) =>
        {
            // Belge henüz yoksa (ör. sunum sırasında üretilemediyse) bağlantıdan önce üretilir.
            var document = await documents.EnsureAsync(id, ct);
            if (!document.IsSuccess)
            {
                return RecordEndpoints.ToProblem(document.Error!);
            }

            var result = await sharing.CreateLinkAsync(id, new QuoteShareRequest(request.DocumentId ?? document.Value!.Id, request.ValidDays, request.RecipientName, request.RecipientEmail), ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var slug = await documents.TenantSlugAsync(ct);
            return Results.Created($"/api/crm/quotes/{id}/share-links",
                new QuoteShareLinkCreatedDto(ToDto(result.Value!.Link), result.Value.Token, $"teklif/{slug}#t={result.Value.Token}"));
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapPost("/{id:guid}/share-links/{linkId:guid}/revoke", async (Guid id, Guid linkId, RevokeQuoteShareLinkRequest request, IQuoteSharing sharing, CancellationToken ct) =>
        {
            var result = await sharing.RevokeLinkAsync(id, linkId, request.Reason, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapGet("/{id:guid}/decisions", async (Guid id, IQuoteSharing sharing, CancellationToken ct) =>
        {
            var result = await sharing.DecisionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(d => new QuoteDecisionDto(
                    d.Id, d.ShareLinkId, d.DocumentId, d.DocumentSha256, d.Decision, d.SignerName, d.SignerEmail, d.SignerTitle, d.Reason, d.TermsStatement,
                    d.ClientAddressMasked, d.At)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);
    }

    // ---- herkese açık uçlar

    private static async Task<IResult> ViewAsync(
        string tenant, PublicQuoteTokenRequest request, HttpContext http, PlatformDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        if (Guard(http) is { } rejected)
        {
            return rejected;
        }

        if (await OpenAsync(tenant, http, platform, scopes, time, ct) is not { } opened)
        {
            return Unknown();
        }

        await using var scope = opened;

        var result = await scope.ServiceProvider.GetRequiredService<IQuoteCustomerPortal>().ViewAsync(request.Token, ct);
        return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : Failure(result.Error, result.Message, result.Fields);
    }

    private static async Task<IResult> PdfAsync(
        string tenant, PublicQuoteTokenRequest request, HttpContext http, PlatformDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        if (Guard(http) is { } rejected)
        {
            return rejected;
        }

        if (await OpenAsync(tenant, http, platform, scopes, time, ct) is not { } opened)
        {
            return Unknown();
        }

        await using var scope = opened;

        var result = await scope.ServiceProvider.GetRequiredService<IQuoteCustomerPortal>().DocumentAsync(request.Token, ct);
        return result.IsSuccess
            ? Results.File(result.Value!.Content, result.Value.ContentType, result.Value.Info.FileName)
            : Failure(result.Error, result.Message, result.Fields);
    }

    private static async Task<IResult> DecisionAsync(
        string tenant, PublicQuoteDecisionRequest request, HttpContext http, PlatformDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        if (Guard(http) is { } rejected)
        {
            return rejected;
        }

        var accept = string.Equals(request.Decision, "accept", StringComparison.OrdinalIgnoreCase);
        if (!accept && !string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["decision"] = ["Karar 'accept' ya da 'reject' olmalıdır."] }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await OpenAsync(tenant, http, platform, scopes, time, ct) is not { } opened)
        {
            return Unknown();
        }

        await using var scope = opened;

        var decision = new QuotePortalDecision(
            request.Token, accept, request.SignerName, request.SignerEmail, request.SignerTitle, request.Reason, request.ConfirmTerms, request.DocumentSha256);
        var client = new QuoteClientInfo(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
        var result = await scope.ServiceProvider.GetRequiredService<IQuoteCustomerPortal>().DecideAsync(decision, client, ct);
        return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : Failure(result.Error, result.Message, result.Fields);
    }

    /// <summary>Gövde sınırı ve önbellek/yönlendirici/dizinleme yasakları; her herkese açık yanıtta uygulanır.</summary>
    private static IResult? Guard(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = MaxBodyBytes;
        }

        return http.Request.ContentLength > MaxBodyBytes ? Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Gövde çok büyük.") : null;
    }

    private static async Task<AsyncServiceScope?> OpenAsync(
        string tenant, HttpContext http, PlatformDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        var slug = tenant.Trim().ToLowerInvariant();
        var found = await platform.Tenants.AsNoTracking()
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active && t.ProvisioningStatus == TenantProvisioningStatus.Active)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (found == Guid.Empty)
        {
            return null;
        }

        var scope = scopes.Create(new TenantContext(found, Guid.Empty, Guid.Empty));

        // Sistem kimliği: izinler yalnızca "crm.quote-customer" aboneliğinin bildirdikleridir (teklifi okuma, karar kaydı).
        var cause = new EventEnvelope(
            Guid.NewGuid(), found, DomainEventTypes.RecordDeleted, 1, CrmModule.Entities.Quote, Guid.Empty, null, Guid.Empty,
            ExecutionSource.Import, http.TraceIdentifier, null, 0, new JsonObject(), time.GetUtcNow());
        scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.quote-customer");
        return scope;
    }

    private static IResult Unknown() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Bağlantı geçersiz.");

    private static IResult Failure(QuotePortalError error, string? message, IReadOnlyDictionary<string, string>? fields) => error switch
    {
        QuotePortalError.Invalid => Results.ValidationProblem(
            (fields ?? new Dictionary<string, string>()).ToDictionary(f => f.Key, f => new[] { f.Value }), title: message, statusCode: StatusCodes.Status400BadRequest),
        QuotePortalError.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: message ?? "Teklif durumu değişti."),
        _ => Unknown(),
    };

    // ---- eşlemeler

    private static QuoteDocumentDto ToDto(QuoteDocumentInfo d) => new(d.Id, d.RevisionNo, d.FileName, d.SizeBytes, d.Sha256, d.TemplateVersion, d.CreatedAt);

    private static QuoteShareLinkDto ToDto(QuoteShareLinkInfo l) => new(
        l.Id, l.DocumentId, l.TokenHint, l.RecipientName, l.RecipientEmail, l.ExpiresAt, l.RevokedAt, l.RevokeReason, l.FirstViewedAt, l.CreatedAt, l.State);

    /// <summary>Belge verisi ile herkese açık sözleşmenin alan adları aynıdır; JSON üzerinden eşlenir (test ile korunur).</summary>
    private static PublicQuoteView ToDto(QuotePortalView v)
    {
        var content = v.Snapshot is null
            ? null
            : JsonSerializer.Deserialize<PublicQuoteContent>(JsonSerializer.Serialize(v.Snapshot, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        return new PublicQuoteView(
            v.State, v.CanDecide, v.Message, v.QuoteNumber, v.RevisionNo, v.DocumentSha256, v.LinkExpiresAt, content, v.AcceptanceStatement,
            v.Decision is null ? null : new PublicQuoteDecisionInfo(v.Decision.Decision, v.Decision.SignerName, v.Decision.At));
    }
}

/// <summary>
/// Teklif belgesini üretir: belge verisi (modül) → PDF (<see cref="QuotePdf"/>) → sabitleme (modül). Satıcı bilgisi tenant ayarlarından gelir
/// (modül ayar altyapısına erişemez).
/// </summary>
internal sealed class QuoteDocumentService(IQuoteSharing sharing, IServiceProvider services, ITenantContextAccessor tenant, PlatformDbContext platform)
{
    public async Task<RecordResult<QuoteDocumentInfo>> EnsureAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var existing = await sharing.ListDocumentsAsync(quoteId, cancellationToken);
        if (!existing.IsSuccess)
        {
            return RecordResult<QuoteDocumentInfo>.Fail(existing.Error!.Code, existing.Error.Message, existing.Error.FieldErrors);
        }

        if (existing.Value!.FirstOrDefault() is { } document)
        {
            return RecordResult<QuoteDocumentInfo>.Ok(document);
        }

        var snapshot = await sharing.BuildSnapshotAsync(quoteId, await SellerAsync(cancellationToken), cancellationToken);
        if (!snapshot.IsSuccess)
        {
            return RecordResult<QuoteDocumentInfo>.Fail(snapshot.Error!.Code, snapshot.Error.Message, snapshot.Error.FieldErrors);
        }

        var pdf = QuotePdf.Render(snapshot.Value!);
        return await sharing.StoreDocumentAsync(quoteId, snapshot.Value!, pdf, QuotePdf.TemplateVersion, cancellationToken);
    }

    public async Task<string> TenantSlugAsync(CancellationToken cancellationToken)
    {
        var id = tenant.Current?.TenantId ?? Guid.Empty;
        return await platform.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => t.Slug).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    private async Task<QuoteSellerInfo> SellerAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<ITenantSettings>();
        var name = (await settings.GetValueAsync(CoreModule.SettingKeys.DisplayName, cancellationToken)).Trim();
        if (name.Length == 0)
        {
            var id = tenant.Current?.TenantId ?? Guid.Empty;
            name = await platform.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        }

        var details = (await settings.GetValueAsync(CrmModule.SettingKeys.QuoteSellerDetails, cancellationToken)).Trim();
        var footer = (await settings.GetValueAsync(CrmModule.SettingKeys.QuoteFooterNote, cancellationToken)).Trim();
        return new QuoteSellerInfo(name, details.Length == 0 ? null : details, footer.Length == 0 ? null : footer);
    }
}
