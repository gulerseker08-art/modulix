using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Modulix.Core.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Platform hesapları için ayarlar (ADR-0028 Dilim 5). <see cref="RequireTwoFactor"/>: platform uç noktaları iki adımlı doğrulaması
/// açık olmayan hesaba kapalıdır (varsayılan açık; yalnızca yerel geliştirme ve testlerde kapatılır).
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public bool RequireTwoFactor { get; set; } = true;
}

/// <summary>
/// Platform uç noktalarının rol tablosu (ADR-0028 Dilim 5): hangi uç hangi rollerden birini ister. <b>Okuma (GET) her platform
/// rolüne açıktır; tabloda olmayan bir yazma ucu yalnızca Owner'a açıktır</b> (varsayılan ret), bu yüzden yeni bir yazma ucu eklenince
/// tabloya yazılmalıdır; <c>PlatformRolesTests</c> bunu denetler.
/// </summary>
public static class PlatformAccessRules
{
    private const PlatformRole Ops = PlatformRole.Operations;
    private const PlatformRole Fin = PlatformRole.Finance;
    private const PlatformRole Sup = PlatformRole.Support;

    private static readonly Dictionary<string, PlatformRole> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /api/platform/tenants"] = Ops,
        ["PUT /api/platform/tenants/{tenantId}"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/close"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/reopen"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/suspend"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/activate"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/administrators"] = Ops,
        ["POST /api/platform/tenants/{tenantId}/profile/{key}"] = Ops,
        ["PUT /api/platform/tenants/{tenantId}/modules/{moduleKey}"] = Ops | Fin,
        ["POST /api/platform/users/reset-two-factor"] = Sup | Ops,
        ["POST /api/platform/events/deliveries/retry"] = Sup | Ops,
        ["POST /api/platform/events/deliveries/skip"] = Sup | Ops,
        ["POST /api/platform/plans"] = Fin,
        ["PUT /api/platform/plans/{planId}"] = Fin,
        ["PUT /api/platform/tenants/{tenantId}/plan"] = Fin,
        ["PUT /api/platform/tenants/{tenantId}/limits/{limitKey}"] = Fin,
        ["POST /api/platform/tenants/{tenantId}/limits/{limitKey}/revoke"] = Fin,
        ["POST /api/platform/team"] = PlatformRole.Owner,
        ["PUT /api/platform/team/{userId}"] = PlatformRole.Owner,
    };

    /// <summary>Platform ekibini listelemek de Owner'a özeldir (kişi ve rol bilgisi).</summary>
    private static readonly HashSet<string> OwnerReads = new(StringComparer.OrdinalIgnoreCase) { "GET /api/platform/team" };

    /// <summary>Tabloda tanımlı yazma uçları (test için).</summary>
    public static IReadOnlyCollection<string> DefinedWrites => Writes.Keys;

    /// <summary>Uç için en az birinin gerektiği roller (hepsi Owner'ı da kabul eder).</summary>
    public static PlatformRole RequiredFor(string method, string? routePattern)
    {
        var key = Key(method, routePattern);
        if (Writes.TryGetValue(key, out var roles))
        {
            return roles;
        }

        if (OwnerReads.Contains(key))
        {
            return PlatformRole.Owner;
        }

        // Okuma her platform rolüne açıktır; tabloda olmayan yazma yalnızca Owner'a.
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            ? PlatformRole.Support | PlatformRole.Operations | PlatformRole.Finance
            : PlatformRole.Owner;
    }

    /// <summary>
    /// "POST /api/platform/tenants/{tenantId}/close" biçimi: yol kısıtları (<c>:guid</c>) ve sondaki "/" atılır (boş yollu grup uçları
    /// "/api/platform/team/" olarak gelir).
    /// </summary>
    public static string Key(string method, string? routePattern) =>
        $"{method.ToUpperInvariant()} {Regex.Replace((routePattern ?? string.Empty).TrimEnd('/'), @"\{(\w+):[^}]+\}", "{$1}")}";
}

/// <summary>
/// Platform uç noktalarının ortak süzgeci: oturum açmış bir platform hesabı, (açıksa) iki adımlı doğrulama ve uç için gereken rol.
/// Bayrak, rol ve doğrulama durumu her istekte veritabanından okunur; değişiklik bir sonraki istekte geçerli olur.
/// </summary>
public sealed class RequirePlatformAdministratorFilter(IPlatformAdministration platform, IOptions<PlatformOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        PlatformAccess? access = Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
            ? await platform.GetAccessAsync(userId, http.RequestAborted)
            : null;
        if (access is null)
        {
            return Forbidden("Bu alan platform yöneticilerine açıktır.");
        }

        if (options.Value.RequireTwoFactor && !access.TwoFactorEnabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Platform işlemleri için iki adımlı doğrulama zorunludur.",
                detail: "Hesabım sayfasından iki adımlı doğrulamayı açın.",
                extensions: new Dictionary<string, object?> { ["code"] = "two-factor-required" });
        }

        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var required = PlatformAccessRules.RequiredFor(http.Request.Method, pattern);
        return PlatformRoles.Allows(access.Roles, required)
            ? await next(context)
            : Forbidden($"Bu işlem için şu platform rollerinden biri gerekir: {string.Join(", ", RoleNames(required))}.");
    }

    private static IEnumerable<string> RoleNames(PlatformRole required) =>
        PlatformRoles.Split(required | PlatformRole.Owner).Select(PlatformRoles.NameOf);

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Bu işlem için yetkiniz yok.", detail: detail);
}
