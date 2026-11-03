using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Shared.Communication;

namespace Modulix.Api.Communication;

/// <summary>
/// Oturumsuz tercih merkezi ve abonelikten çıkma uç noktaları (<c>/api/public/communication/{tenant}</c>; ADR-0025, dilim 6). Kimlik doğrulama bağlantı
/// belirtecine sahip olmaktır; belirteç yalnızca o adresin pazarlama tercihini değiştirebilir. Sayfa uçlarında belirteç gövdede taşınır (adresin # parçasından
/// gelir); tek tıkla çıkış (RFC 8058) uçunda yolda taşınır, çünkü posta istemcisi bu adrese POST eder. Hız sınırı, kısa gövde sınırı ve tek tip hata uygulanır.
/// Bu uçlar yalnızca POST'tur: bağlantıyı önceden açan güvenlik tarayıcıları (GET) kimseyi abonelikten çıkaramaz.
/// </summary>
internal static class CommunicationPublicEndpoints
{
    public const string Policy = "communication-public";
    private const int BodyMaxBytes = 4 * 1024;

    public static IServiceCollection AddCommunicationPublicRateLimit(this IServiceCollection services, IConfiguration configuration) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var permits = Math.Max(2, configuration.GetValue("CommunicationPublic:RateLimitPerMinute", 30));
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"communication|{context.Connection.RemoteIpAddress}|{context.Request.RouteValues["tenant"]}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static IEndpointRouteBuilder MapCommunicationPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/communication/{tenant}").AllowAnonymous().WithTags("İletişim tercih merkezi (herkese açık)");
        group.MapPost("/view", (string tenant, PublicCommunicationTokenRequest request, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, CancellationToken ct) =>
            RunAsync(tenant, request.Token, http, platform, scopes, (portal, token) => portal.ViewAsync(token, ct), ct)).RequireRateLimiting(Policy);
        group.MapPost("/unsubscribe", (string tenant, PublicCommunicationTokenRequest request, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, CancellationToken ct) =>
            RunAsync(tenant, request.Token, http, platform, scopes, (portal, token) => portal.UnsubscribeAsync(token, ct), ct)).RequireRateLimiting(Policy);
        group.MapPost("/resubscribe", (string tenant, PublicCommunicationTokenRequest request, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, CancellationToken ct) =>
            RunAsync(tenant, request.Token, http, platform, scopes, (portal, token) => portal.ResubscribeAsync(token, ct), ct)).RequireRateLimiting(Policy);

        group.MapPost("/topic", (string tenant, PublicTopicRequest request, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, CancellationToken ct) =>
            RunAsync(tenant, request.Token, http, platform, scopes, (portal, token) => portal.SetTopicAsync(token, request.TopicKey, request.Subscribed, ct), ct)).RequireRateLimiting(Policy);

        // RFC 8058 tek tıkla çıkış: posta istemcisi "List-Unsubscribe=One-Click" gövdesiyle POST eder. Form bağlamayı (ve çerez tabanlı sahte istek
        // korumasını) kullanmaz: oturum çerezi yoktur, kimlik belirteçtir. Gövde elle ve sınırlı okunur.
        group.MapPost("/one-click/{token}", async (string tenant, string token, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, CancellationToken ct) =>
        {
            if (Guard(http) is { } rejected)
            {
                return rejected;
            }

            var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(ct) : null;
            if (form is null || !form.TryGetValue("List-Unsubscribe", out var value) || value != "One-Click")
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Tek tıkla çıkış isteği geçersiz.");
            }

            return await RunAsync(tenant, token, http, platform, scopes, (portal, t) => portal.UnsubscribeAsync(t, ct), ct, oneClick: true);
        }).RequireRateLimiting(Policy).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> RunAsync(
        string tenant, string token, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes,
        Func<ICommunicationPortal, string, Task<PortalResult>> action, CancellationToken cancellationToken, bool oneClick = false)
    {
        if (!oneClick && Guard(http) is { } rejected)
        {
            return rejected;
        }

        var key = tenant.Trim().ToLowerInvariant();
        var query = platform.Tenants.AsNoTracking().Where(t => TenantStatusRules.Operational.Contains(t.Status));
        var found = Guid.TryParseExact(key, "N", out var id)
            ? await query.Where(t => t.Id == id).Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(cancellationToken)
            : await query.Where(t => t.Slug == key).Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return Unknown();
        }

        // Kullanıcı ve üyelik yoktur (Guid.Empty); yalnızca bu tenant'ın satır güvenliği altında, belirtecin adresinin tercihi değişir.
        await using var scope = scopes.Create(new TenantContext(found.Id, Guid.Empty, Guid.Empty));
        var result = await action(scope.ServiceProvider.GetRequiredService<ICommunicationPortal>(), token);
        if (result.Error == PortalError.Invalid)
        {
            return Unknown();
        }

        if (result.Error == PortalError.Refused)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message ?? "İşlem yapılamadı.");
        }

        var view = result.View!;
        return Results.Ok(new PublicCommunicationView(
            view.Channel.ToString(), view.MaskedAddress, view.State, view.CanUnsubscribe, view.CanResubscribe, view.Message, found.Name, view.ExpiresAt,
            view.Topics?.Select(t => new PublicTopicDto(t.Key, t.Name, t.Subscribed, t.CanChange)).ToList()));
    }

    private static IResult? Guard(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = BodyMaxBytes;
        }

        return http.Request.ContentLength > BodyMaxBytes ? Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Gövde çok büyük.") : null;
    }

    private static IResult Unknown() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Bağlantı geçersiz.");
}
