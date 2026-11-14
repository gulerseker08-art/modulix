using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modulix.Api.Observability;
using Modulix.Shared.Auth;
using OpenTelemetry.Trace;

namespace Modulix.IntegrationTests;

/// <summary>Gözlemlenebilirlik (A2): log kapsamında tenant, kullanıcı ve iz kimliği; OpenTelemetry yalnızca adres verilince.</summary>
public class ObservabilityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Logs_of_a_signed_in_request_carry_tenant_user_and_trace()
    {
        var logs = new CapturingLoggerProvider();
        using var host = WithLogs(logs);
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(host, user.Email!);

        var response = await client.GetAsync("/api/tenant/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // "Executing endpoint" ve "Executed endpoint" satırları; ikisi de kapsamın içindedir.
        var entry = logs.Entries.FirstOrDefault(e =>
            e.Category.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal)
            && e.Message.Contains("/api/tenant/current", StringComparison.Ordinal));
        Assert.NotNull(entry);
        Assert.Equal(tenant.Id, entry.Scope[RequestLogScopeMiddleware.TenantIdKey]);
        Assert.Equal(user.Id, entry.Scope[RequestLogScopeMiddleware.UserIdKey]);
        Assert.True(entry.Scope.ContainsKey(RequestLogScopeMiddleware.MembershipIdKey));
        Assert.True(entry.Scope.ContainsKey("TraceId"));
    }

    [Fact]
    public async Task Anonymous_requests_get_no_tenant_scope()
    {
        var logs = new CapturingLoggerProvider();
        using var host = WithLogs(logs);
        using var client = host.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($"yok-{Guid.NewGuid():N}@test.modulix", "Yanlis.Sifre.2026"));

        Assert.DoesNotContain(logs.Entries, e => e.Scope.ContainsKey(RequestLogScopeMiddleware.TenantIdKey) || e.Scope.ContainsKey(RequestLogScopeMiddleware.UserIdKey));
    }

    [Fact]
    public void OpenTelemetry_is_off_without_an_endpoint()
    {
        Assert.Null(factory.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void OpenTelemetry_is_on_with_an_endpoint()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("OpenTelemetry:OtlpEndpoint", "http://localhost:4317"));

        Assert.NotNull(host.Services.GetService<TracerProvider>());
    }

    // Uç nokta yürütme logları (Microsoft.AspNetCore.Routing, Information) kapsamın içinde yazılır; varsayılanda Warning'dir.
    private WebApplicationFactory<Program> WithLogs(CapturingLoggerProvider logs) =>
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Routing", "Information")
            .ConfigureLogging(logging => logging.AddProvider(logs)));

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, string email)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private sealed record LogEntry(string Category, string Message, IReadOnlyDictionary<string, object?> Scope);

    /// <summary>Yazılan her log satırını, o andaki kapsam değerleriyle birlikte saklar.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider.scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, object?>();
                provider.scopes.ForEachScope(
                    (scope, target) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        {
                            foreach (var pair in pairs)
                            {
                                target[pair.Key] = pair.Value;
                            }
                        }
                    },
                    values);
                provider.Entries.Enqueue(new LogEntry(category, formatter(state, exception), values));
            }
        }
    }
}
