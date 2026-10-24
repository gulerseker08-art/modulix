using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Api.Platform;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Shared.Auth;

namespace Modulix.Api.Tenancy;

public static class ModulixClaimTypes
{
    /// <summary>Aktif tenant. Yalnızca sunucu yazar (şifreli oturum çerezinde); her istekte yeniden doğrulanır.</summary>
    public const string TenantId = "modulix:tenant_id";

    /// <summary>Sunucudaki oturum (Y7a, <c>platform.UserSessions</c>); her istekte kapatılmadığı denetlenir.</summary>
    public const string SessionId = "modulix:session_id";
}

/// <summary>İstek başına aktif tenant bağlamı; <see cref="TenantContextMiddleware"/> doldurur.</summary>
public sealed class HttpTenantContextAccessor : ITenantContextAccessor
{
    public TenantContext? Current { get; set; }

    /// <summary>
    /// Oturumdaki firma iki adımlı doğrulamayı zorunlu kılıyor ve kişinin doğrulaması kapalı (Y7b): bağlam oluşmaz,
    /// firma uç noktaları 403 ve <see cref="TenantProblems.TwoFactorRequired"/> döner; kişi önce kurulumu yapar.
    /// </summary>
    public Guid? TwoFactorRequiredTenantId { get; set; }
}

public static class TenantProblems
{
    /// <summary>403 sorun ayrıntısının <c>code</c> alanı: firma iki adımlı doğrulama istiyor.</summary>
    public const string TwoFactorRequired = "two-factor-required";
}

/// <summary>
/// Oturumdaki tenant iddiasını her istekte veritabanından doğrular. Üyelik devre dışı bırakılır
/// veya tenant kapatılırsa bağlam oluşmaz ve tenant gerektiren uç noktalar 403 döner.
/// İstemcinin gönderdiği bir başlık veya parametre tenant belirlemez.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, HttpTenantContextAccessor accessor, IMembershipDirectory directory, ITenantSettings settings, ModulixDbContext db)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirst(ModulixClaimTypes.TenantId)?.Value, out var tenantId)
            && Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            var membership = await directory.FindActiveAsync(userId, tenantId, context.RequestAborted);
            if (membership is not null)
            {
                accessor.Current = new TenantContext(membership.TenantId, userId, membership.MembershipId);

                // Firma iki adımlı doğrulamayı zorunlu kılıyorsa kapalı olan kişi önce kurulumu yapar (Y7b).
                if (await settings.GetValueAsync(CoreModule.SettingKeys.RequireTwoFactor, context.RequestAborted) == CoreModule.SettingValues.Yes
                    && !await db.Users.AnyAsync(u => u.Id == userId && u.TwoFactorEnabled, context.RequestAborted))
                {
                    accessor.Current = null;
                    accessor.TwoFactorRequiredTenantId = membership.TenantId;
                }
            }
        }

        await next(context);
    }
}

/// <summary>Aktif ve doğrulanmış tenant yoksa isteği 403 ile durdurur.</summary>
public sealed class RequireTenantFilter(HttpTenantContextAccessor tenant) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        tenant.Current is not null
            ? next(context)
            : ValueTask.FromResult<object?>(tenant.TwoFactorRequiredTenantId is not null
                ? Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Bu firma iki adımlı doğrulama istiyor.",
                    detail: "Devam etmeden önce Hesabım sayfasından iki adımlı doğrulamayı açın.",
                    extensions: new Dictionary<string, object?> { ["code"] = TenantProblems.TwoFactorRequired })
                : Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Aktif tenant yok.",
                    detail: "Bir tenant seçin. Üyeliğiniz kaldırıldıysa bu tenant'a artık erişemezsiniz."));
}

public static class TenancySetup
{
    public static IServiceCollection AddModulixTenancy(this IServiceCollection services)
    {
        services.AddScoped<HttpTenantContextAccessor>();
        services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<HttpTenantContextAccessor>());
        services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
        return services;
    }

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenant/current", async (ITenantContextAccessor tenant, IMembershipDirectory directory, CancellationToken ct) =>
            {
                var current = tenant.Current!;
                var membership = await directory.FindActiveAsync(current.UserId, current.TenantId, ct);
                return membership is null
                    ? Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Aktif tenant yok.")
                    : Results.Ok(new CurrentTenantResponse(membership.TenantId, membership.TenantName, membership.TenantSlug));
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Tenant");

        // Sorumlu seçimi için üye adları; her üye görebilir (e-posta ve rol bilgisi verilmez). assignable=true: yalnızca
        // kişinin kayıt kapsamındaki üyeler (Y8), yani sorumlu olarak atayabileceği ve süzgeçte seçebileceği kişiler.
        app.MapGet("/api/tenant/members", async (
                bool? assignable, ITenantMembers members, IDataScopeService scope, ITenantContextAccessor tenant, CancellationToken ct) =>
            {
                var owners = assignable == true ? await scope.GetOwnersAsync(tenant.Current!, ct) : null;
                return (await members.ListActiveAsync(ct))
                    .Where(m => owners is null || owners.Contains(m.UserId))
                    .Select(m => new TenantMemberResponse(m.UserId, m.DisplayName))
                    .ToList();
            })
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Tenant");

        return app;
    }
}
