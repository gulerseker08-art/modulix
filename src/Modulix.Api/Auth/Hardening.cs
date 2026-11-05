using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace Modulix.Api.Auth;

/// <summary>
/// Genel sertleştirme: ters vekil arkasında istemci IP'si, oturum açmayan kimlik uçlarının hız sınırı ve
/// tarayıcı güvenlik başlıkları. Hepsi yapılandırmayla çalışır (on-prem: tek imaj, müşteriye özel kod yok).
/// </summary>
public static class Hardening
{
    /// <summary>Oturumsuz kimlik uçlarının (giriş, şifre sıfırlama, davet) hız sınırı ilkesi.</summary>
    public const string AuthRateLimitPolicy = "auth-anonymous";

    public const string ForwardedHeadersSection = "ForwardedHeaders";

    /// <summary>
    /// <c>ForwardedHeaders:Enabled=true</c> ise <c>X-Forwarded-For/Proto</c> yalnızca güvenilen vekillerden kabul edilir:
    /// <c>ForwardedHeaders:KnownProxies</c> (IP listesi) ve <c>ForwardedHeaders:KnownNetworks</c> (CIDR listesi).
    /// Liste verilmezse yalnızca yerel vekil (loopback) güvenilir; böylece başlık sahteciliğiyle hız sınırı aşılamaz.
    /// </summary>
    public static IServiceCollection AddModulixHardening(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ForwardedHeadersSection);
        if (section.GetValue("Enabled", false))
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;

                foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }

                foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
                {
                    if (System.Net.IPNetwork.TryParse(network, out var parsed))
                    {
                        options.KnownIPNetworks.Add(parsed);
                    }
                }
            });
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var permits = Math.Max(1, configuration.GetValue("Auth:RateLimitPerMinute", 30));
            options.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"auth|{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }

    /// <summary>İstemci IP'sini vekil başlığından okur (yapılandırma açıksa); hız sınırından ve kimlik doğrulamadan önce çalışmalıdır.</summary>
    public static IApplicationBuilder UseModulixForwardedHeaders(this WebApplication app)
    {
        if (app.Configuration.GetSection(ForwardedHeadersSection).GetValue("Enabled", false))
        {
            app.UseForwardedHeaders();
        }

        return app;
    }

    /// <summary>
    /// Güvenlik başlıkları. Uç noktanın kendi koyduğu başlık (ör. imzalı belge yanıtlarının CSP'si) ezilmez.
    /// CSP yalnızca gömme/form/temel adres kısıtlar; betik kaynağı kısıtlamaz (Blazor WebAssembly'yi bozmamak için).
    /// </summary>
    public static IApplicationBuilder UseModulixSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");
                headers.TryAdd("Content-Security-Policy", "frame-ancestors 'self'; base-uri 'self'; object-src 'none'; form-action 'self'");
                return Task.CompletedTask;
            });

            await next(context);
        });
}
