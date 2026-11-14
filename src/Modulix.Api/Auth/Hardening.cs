using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Modulix.Api.Tenancy;

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

            // Oturum açmış istekler için genel sınır: kişi başına ve firma başına (tek paylaşımlı veritabanında bir hesabın
            // ya da bir firmanın yükü diğer firmaları yavaşlatmasın). Oturumsuz uçlar kendi adlandırılmış ilkeleriyle sınırlıdır.
            var perUser = configuration.GetValue($"{RateLimitingSection}:UserPerMinute", 600);
            var perTenant = configuration.GetValue($"{RateLimitingSection}:TenantPerMinute", 3000);
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => SignedInPartition(context, "user", ClaimTypes.NameIdentifier, perUser)),
                PartitionedRateLimiter.Create<HttpContext, string>(context => SignedInPartition(context, "tenant", ModulixClaimTypes.TenantId, perTenant)));

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// Genel sınırın ayarları: <c>RateLimiting:UserPerMinute</c> (varsayılan 600) ve <c>RateLimiting:TenantPerMinute</c>
    /// (varsayılan 3000). 0 ya da negatif değer o sınırı kapatır.
    /// </summary>
    public const string RateLimitingSection = "RateLimiting";

    // Yalnızca oturum açmış API istekleri sayılır; iddia değeri şifreli oturum çerezinden gelir (istemci seçemez).
    private static RateLimitPartition<string> SignedInPartition(HttpContext context, string kind, string claimType, int permitsPerMinute)
    {
        if (permitsPerMinute <= 0
            || !context.Request.Path.StartsWithSegments("/api")
            || context.User.FindFirstValue(claimType) is not { Length: > 0 } key)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{kind}|{key}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
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

    /// <summary>Zorunlu CSP: gömme, form hedefi, temel adres ve eklenti kısıtları.</summary>
    public const string EnforcedCsp = "frame-ancestors 'self'; base-uri 'self'; object-src 'none'; form-action 'self'";

    /// <summary>
    /// Raporlanan CSP: betikler yalnızca aynı kaynaktan (WebAssembly derlemesi için <c>'wasm-unsafe-eval'</c>), ağ istekleri
    /// yalnızca aynı kaynağa. Arayüz dış kaynak kullanmaz.
    /// </summary>
    public const string ReportOnlyCsp = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";

    /// <summary>
    /// Güvenlik başlıkları. Uç noktanın kendi koyduğu başlık (ör. imzalı belge yanıtlarının CSP'si) ezilmez.
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
                headers.TryAdd("Content-Security-Policy", EnforcedCsp);

                // Betik ve bağlantı kısıtı önce yalnızca raporlanır: arayüzün satır içi import map'i (Blazor parmak izi)
                // tarayıcı konsolunda ihlal olarak görünür; karma (hash) ile izin verilip zorunlu hale getirilmesi sonraki adımdır.
                headers.TryAdd("Content-Security-Policy-Report-Only", ReportOnlyCsp);
                return Task.CompletedTask;
            });

            await next(context);
        });
}
