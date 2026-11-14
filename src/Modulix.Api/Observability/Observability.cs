using System.Reflection;
using System.Security.Claims;
using Modulix.Api.Tenancy;
using Modulix.Infrastructure.Events;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Modulix.Api.Observability;

/// <summary>
/// Gözlemlenebilirlik (A2, Ç14.5): log satırları istek izine (TraceId/SpanId) ve kapsam olarak tenant ile kullanıcıya
/// bağlanır; OTLP adresi verilirse izler ve metrikler OpenTelemetry ile dışa aktarılır. Log biçimi yapılandırmadan
/// gelir (<c>Logging:Console:FormatterName</c>; üretimde <c>json</c>). Hiçbir log kapsamına kişisel veri ya da kayıt
/// içeriği konmaz; yalnızca kimlikler.
/// </summary>
public static class Observability
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>OpenTelemetry kaynağındaki hizmet adı.</summary>
    public const string ServiceName = "modulix-api";

    public static WebApplicationBuilder AddModulixObservability(this WebApplicationBuilder builder)
    {
        // TraceId ve SpanId her log satırının kapsamına girer; denetim kaydındaki CorrelationId ile aynı değerdir.
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

        if (OtlpEndpoint(builder.Configuration) is not { } endpoint)
        {
            return builder;
        }

        var version = typeof(Observability).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(EventMetrics.MeterName))
            .UseOtlpExporter(OtlpProtocol(builder.Configuration), endpoint);

        return builder;
    }

    /// <summary>
    /// OTLP adresi: <c>OpenTelemetry:OtlpEndpoint</c> ya da standart <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. İkisi de yoksa
    /// OpenTelemetry kaydedilmez (ek yük yok); loglar yine TraceId taşır.
    /// </summary>
    public static Uri? OtlpEndpoint(IConfiguration configuration)
    {
        var value = configuration[$"{SectionName}:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    // "grpc" (varsayılan, 4317) ya da "http/protobuf" (4318).
    private static OpenTelemetry.Exporter.OtlpExportProtocol OtlpProtocol(IConfiguration configuration) =>
        string.Equals(configuration[$"{SectionName}:OtlpProtocol"], "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
            : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
}

/// <summary>
/// İsteğin geri kalanında yazılan her log satırına tenant, üyelik ve kullanıcı kimliğini kapsam olarak ekler.
/// <see cref="TenantContextMiddleware"/>'dan sonra çalışır: tenant doğrulanmamışsa yalnızca kullanıcı kimliği eklenir.
/// </summary>
public sealed class RequestLogScopeMiddleware(RequestDelegate next, ILogger<RequestLogScopeMiddleware> logger)
{
    public const string TenantIdKey = "TenantId";
    public const string UserIdKey = "UserId";
    public const string MembershipIdKey = "MembershipId";

    public async Task InvokeAsync(HttpContext context, HttpTenantContextAccessor accessor)
    {
        var scope = new Dictionary<string, object>(3);
        if (accessor.Current is { } tenant)
        {
            scope[TenantIdKey] = tenant.TenantId;
            scope[MembershipIdKey] = tenant.MembershipId;
            scope[UserIdKey] = tenant.UserId;
        }
        else if (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            scope[UserIdKey] = userId;
        }

        if (scope.Count == 0)
        {
            await next(context);
            return;
        }

        using (logger.BeginScope(scope))
        {
            await next(context);
        }
    }
}
