using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Api.Users;
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

        // Listeleme e-posta durumunu kuyruktan okur ve teklifin gönderim durumunu buna eşitler (arka plan işleyicisi tenant DB'sine yazmaz).
        quotes.MapGet("/{id:guid}/share-links", async (Guid id, IQuoteSharing sharing, QuoteDelivery delivery, CancellationToken ct) =>
        {
            var result = await sharing.ListLinksAsync(id, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var states = await SyncDeliveryAsync(id, result.Value!, delivery, sharing, ct);
            return Results.Ok(result.Value!.Select(l => ToDto(l, states.GetValueOrDefault(l.Id))).ToList());
        }).RequirePermission(CrmModule.Permissions.QuotesRead);

        quotes.MapPost("/{id:guid}/share-links", async (Guid id, CreateQuoteShareLinkRequest request, QuoteDocumentService documents, IQuoteSharing sharing,
            QuoteDelivery delivery, IRecordPipeline pipeline, ILogger<QuoteDelivery> log, HttpContext http, CancellationToken ct) =>
        {
            // E-posta isteniyorsa alıcı adresi zorunludur; bağlantı oluşmadan reddedilir.
            if (request.SendEmail && string.IsNullOrWhiteSpace(request.RecipientEmail))
            {
                var message = "E-posta göndermek için alıcı e-postası girin.";
                return RecordEndpoints.ToProblem(new RecordError(RecordErrorCode.ValidationFailed, message, [new RecordFieldError("recipientEmail", message)]));
            }

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
            var path = $"teklif/{slug}#t={result.Value!.Token}";
            QuoteLinkDelivery? state = null;
            if (request.SendEmail)
            {
                // Bağlantı oluştu; e-posta kuyruğa yazılamazsa durum dürüstçe "Failed" gösterilir (bağlantı yine de elle iletilebilir).
                try
                {
                    var quote = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, id), ct);
                    var dto = CrmEndpoints.ToDto<QuoteDto>(quote.Value!);
                    delivery.Enqueue(result.Value.Link.Id, request.RecipientEmail!.Trim(), request.RecipientName, (await documents.SellerNameAsync(ct)),
                        dto.QuoteNumber, dto.Title, dto.ValidUntil, $"{PublicUrl.Resolve(http)}/{path}");
                    state = (await delivery.GetAsync([result.Value.Link.Id], ct)).GetValueOrDefault(result.Value.Link.Id)
                        ?? new QuoteLinkDelivery(QuoteDeliveryStatus.Pending, null, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogWarning(exception, "Teklif {QuoteId} bağlantı e-postası kuyruğa yazılamadı.", id);
                    state = new QuoteLinkDelivery(QuoteDeliveryStatus.Failed, "E-posta kuyruğa yazılamadı.", null);
                }

                var all = await sharing.ListLinksAsync(id, ct);
                if (all.IsSuccess)
                {
                    await SyncDeliveryAsync(id, all.Value!, delivery, sharing, ct, (result.Value.Link.Id, state));
                }
            }

            return Results.Created($"/api/crm/quotes/{id}/share-links", new QuoteShareLinkCreatedDto(ToDto(result.Value.Link, state), result.Value.Token, path));
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

    private static QuoteShareLinkDto ToDto(QuoteShareLinkInfo l, QuoteLinkDelivery? delivery = null) => new(
        l.Id, l.DocumentId, l.TokenHint, l.RecipientName, l.RecipientEmail, l.ExpiresAt, l.RevokedAt, l.RevokeReason, l.FirstViewedAt, l.CreatedAt, l.State,
        delivery?.Status ?? QuoteDeliveryStatus.NotSent, delivery?.Error, delivery?.ProviderAcceptedAt);

    /// <summary>
    /// Bağlantıların e-posta durumunu kuyruktan okur ve teklifin gönderim durumunu (e-postası olan en son bağlantının durumu) başlığa işler.
    /// Başlık yazımı başarısız olsa da liste dönebilir; durum bir sonraki okumada yeniden eşitlenir.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, QuoteLinkDelivery>> SyncDeliveryAsync(
        Guid quoteId, IReadOnlyList<QuoteShareLinkInfo> links, QuoteDelivery delivery, IQuoteSharing sharing, CancellationToken ct,
        (Guid LinkId, QuoteLinkDelivery? State)? known = null)
    {
        var states = (await delivery.GetAsync(links.Select(l => l.Id), ct)).ToDictionary(p => p.Key, p => p.Value);
        if (known is { State: { } state })
        {
            states[known.Value.LinkId] = state;
        }

        var overall = QuoteDelivery.Overall(links.Select(l => (l.CreatedAt, states.GetValueOrDefault(l.Id))));
        await sharing.SetDeliveryStatusAsync(quoteId, overall, $"Gönderim durumu: {QuoteDelivery.Label(overall)}", ct);
        return states;
    }

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

    public async Task<string> SellerNameAsync(CancellationToken cancellationToken)
    {
        var name = (await SellerAsync(cancellationToken)).Name;
        return name.Length > 100 ? name[..100] : name;
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
