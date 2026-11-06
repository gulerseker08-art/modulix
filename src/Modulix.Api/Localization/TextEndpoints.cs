using Modulix.Core.Contracts.Localization;
using Modulix.Core.Settings;
using Modulix.Shared.Localization;

namespace Modulix.Api.Localization;

/// <summary>
/// Arayüz metinleri (ADR-0034): <c>GET /api/i18n/{dil}</c>. Oturumsuzdur, çünkü giriş ekranı da çevrilir; yalnızca ürünün
/// sabit metinlerini döner (tenant ya da kullanıcı verisi içermez). Bilinmeyen dil 404 döner.
/// </summary>
public static class TextEndpoints
{
    public static IEndpointRouteBuilder MapTextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/i18n/{language}", (string language, ITextCatalog catalog, HttpContext http) =>
        {
            if (LanguageOptions.Normalize(language) is not { } code)
            {
                return Results.NotFound();
            }

            // Sözlükler yalnızca yeni sürümle değişir; kısa süre önbelleğe alınabilir.
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Ok(new TextBundleResponse(code, catalog.Bundle(code)));
        }).AllowAnonymous().WithTags("Localization");

        return app;
    }
}
