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
using Modulix.Core.Platform;
using Modulix.Core.Events;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Leads;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Herkese açık Lead toplama ucu: <c>POST /api/public/lead-forms/{tenant}/{formKey}</c> ve CORS ön kontrolü (<c>OPTIONS</c>).
/// <list type="bullet">
///   <item><b>Tenant</b> URL'deki firma kısa adından sunucuda çözülür; gövdede/başlıkta tenant kimliği aranmaz. İstek, oturumdan ve
///   çerezden bağımsız, "crm.lead-intake" sistem kimliğiyle (dar izinler) kendi tenant kapsamında çalışır.</item>
///   <item><b>Form kimliği gizli değildir</b> (sayfada görünür); tek başına yalnızca izinli kaynaktan ve yalnızca izinli alanlarla Lead
///   oluşturma imkânı verir. Sunucudan sunucuya çağrılar <c>X-Lead-Form-Secret</c> başlığını kullanır; sır tarayıcıya konmaz.</item>
///   <item><b>Origin/CORS</b> yalnızca tarayıcıların başka siteden gönderimini sınırlar; kimlik doğrulama değildir.</item>
///   <item>Hız sınırı (IP + form başına), 32 KB gövde sınırı, alan/uzunluk doğrulaması, bal tuzağı ve tekrar engeli uygulanır. Yanıt, kişinin
///   ya da e-postanın sistemde olup olmadığını belli etmez.</item>
/// </list>
/// </summary>
internal static class LeadIntakeEndpoints
{
    public const string RateLimitPolicy = "lead-intake";
    public const string SecretHeader = "X-Lead-Form-Secret";
    public const int MaxBodyBytes = 32 * 1024;

    /// <summary>IP + form başına dakikadaki gönderim sınırı (<c>LeadIntake:RateLimitPerMinute</c>, varsayılan 20).</summary>
    public static void AddLeadIntakeRateLimit(this IServiceCollection services, IConfiguration configuration) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var permits = Math.Max(1, configuration.GetValue("LeadIntake:RateLimitPerMinute", 20));
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"{context.Connection.RemoteIpAddress}|{context.Request.RouteValues["tenant"]}|{context.Request.RouteValues["formKey"]}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static IEndpointRouteBuilder MapLeadIntakeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/lead-forms/{tenant}/{formKey}")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("Müşteri adayı formu (herkese açık)");
        group.MapPost("", SubmitAsync);
        group.MapMethods("", ["OPTIONS"], PreflightAsync);

        // Form yönetimi (oturumlu): izin crm.lead-forms.manage.
        var admin = app.MapGroup("/api/crm").RequireAuthorization().RequireModule(CrmModule.ModuleKey).WithTags("CRM");
        CrmEndpoints.MapResource<LeadFormDto, SaveLeadFormRequest>(admin, "lead-forms", CrmModule.Entities.LeadForm,
            CrmModule.Permissions.LeadFormsManage, CrmModule.Permissions.LeadFormsManage);
        admin.MapPost("/lead-forms/{id:guid}/secret", RotateSecretAsync).RequirePermission(CrmModule.Permissions.LeadFormsManage);
        return app;
    }

    private static async Task<IResult> RotateSecretAsync(Guid id, LeadVersionRequest request, ILeadFormAdmin admin, CancellationToken ct)
    {
        var result = await admin.RotateSecretAsync(id, request.Version, ct);
        return result.IsSuccess
            ? Results.Ok(new LeadFormSecretDto(CrmEndpoints.ToDto<LeadFormDto>(result.Value!.Form), result.Value.Secret))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> SubmitAsync(
        string tenant, string formKey, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        // Gövde sınırı (Kestrel); salt okunur olduğunda (test sunucusu) uygulanamaz.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "Gövde çok büyük.");
        }


        if (!http.Request.HasJsonContentType())
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "JSON gönderin.");
        }

        JsonObject? fields;
        try
        {
            fields = await http.Request.ReadFromJsonAsync<JsonObject>(JsonSerializerOptions.Web, ct);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Geçersiz istek gövdesi.");
        }

        if (fields is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Geçersiz istek gövdesi.");
        }

        var slug = tenant.Trim().ToLowerInvariant();
        var found = await platform.Tenants.AsNoTracking()
            .Where(t => t.Slug == slug && TenantStatusRules.Operational.Contains(t.Status))
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (found == Guid.Empty)
        {
            return Problem(StatusCodes.Status404NotFound, "Form bulunamadı.");
        }

        await using var scope = scopes.Create(new TenantContext(found, Guid.Empty, Guid.Empty));

        // Sistem kimliği: izinler yalnızca "crm.lead-intake" aboneliğinin bildirdikleridir; kayıt boru hattı bunu uygular.
        var cause = new EventEnvelope(
            Guid.NewGuid(), found, DomainEventTypes.RecordDeleted, 1, CrmModule.Entities.LeadForm, Guid.Empty, null, Guid.Empty,
            ExecutionSource.Import, http.TraceIdentifier, null, 0, new JsonObject(), time.GetUtcNow());
        scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.lead-intake");
        var intake = scope.ServiceProvider.GetRequiredService<ILeadIntake>();

        var origin = http.Request.Headers.Origin.ToString();
        var secret = http.Request.Headers[SecretHeader].ToString();
        var result = await intake.SubmitAsync(new LeadIntakeSubmission(formKey, origin, secret, fields), ct);

        if (await intake.AllowedOriginAsync(formKey, origin, ct) is { Length: > 0 } allowed)
        {
            AddCors(http, allowed);
        }

        return result.Outcome switch
        {
            LeadIntakeOutcome.Accepted => Results.Json(new PublicLeadFormResponse(true, null), statusCode: StatusCodes.Status202Accepted),
            LeadIntakeOutcome.Invalid => Results.ValidationProblem(
                result.Errors!.ToDictionary(e => e.Key, e => e.Value), title: "Form gönderilemedi.", statusCode: StatusCodes.Status400BadRequest),
            LeadIntakeOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "Form bulunamadı."),
            _ => Problem(StatusCodes.Status403Forbidden, "Gönderim kabul edilmedi."),
        };
    }

    private static async Task<IResult> PreflightAsync(
        string tenant, string formKey, HttpContext http, ModulixDbContext platform, ITenantScopeFactory scopes, TimeProvider time, CancellationToken ct)
    {
        var origin = http.Request.Headers.Origin.ToString();
        var slug = tenant.Trim().ToLowerInvariant();
        var found = await platform.Tenants.AsNoTracking()
            .Where(t => t.Slug == slug && TenantStatusRules.Operational.Contains(t.Status)).Select(t => t.Id).FirstOrDefaultAsync(ct);
        if (found != Guid.Empty && origin.Length > 0)
        {
            await using var scope = scopes.Create(new TenantContext(found, Guid.Empty, Guid.Empty));
            var cause = new EventEnvelope(
                Guid.NewGuid(), found, DomainEventTypes.RecordDeleted, 1, CrmModule.Entities.LeadForm, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, http.TraceIdentifier, null, 0, new JsonObject(), time.GetUtcNow());
            scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.lead-intake");
            if (await scope.ServiceProvider.GetRequiredService<ILeadIntake>().AllowedOriginAsync(formKey, origin, ct) is { Length: > 0 } allowed)
            {
                AddCors(http, allowed);
                http.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
                http.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                http.Response.Headers.AccessControlMaxAge = "600";
            }
        }

        return Results.NoContent();
    }

    private static void AddCors(HttpContext http, string origin)
    {
        http.Response.Headers.AccessControlAllowOrigin = origin;
        http.Response.Headers.Vary = "Origin";
    }

    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}
