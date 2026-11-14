using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>Genel sertleştirme: oturumsuz kimlik uçlarının hız sınırı ve tarayıcı güvenlik başlıkları.</summary>
public class HardeningTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Login_is_rate_limited_per_ip()
    {
        using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:RateLimitPerMinute", "3"));
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($"yok{i}@hardening.test", "Yanlis.Sifre.2026"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync("/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", response.Headers.GetValues("Content-Security-Policy-Report-Only").Single());
    }

    [Fact]
    public async Task Signed_in_api_calls_are_rate_limited_per_user()
    {
        using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:UserPerMinute", "3"));
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var other = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(limited, user.Email!);
        using var otherClient = await LoginAsync(limited, other.Email!);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.GetAsync("/api/auth/me")).StatusCode);
        }

        // Giriş isteği oturumsuz olduğu için sayılmaz; sınır kişiye özeldir, diğer kullanıcı etkilenmez.
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        var rejected = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Signed_in_api_calls_are_rate_limited_per_tenant()
    {
        using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:TenantPerMinute", "4"));
        var tenant = await factory.CreateTenantAsync();
        var otherTenant = await factory.CreateTenantAsync();
        var first = await factory.CreateUserAsync(tenant);
        var second = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(otherTenant);
        using var firstClient = await LoginAsync(limited, first.Email!);
        using var secondClient = await LoginAsync(limited, second.Email!);
        using var outsiderClient = await LoginAsync(limited, outsider.Email!);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await firstClient.GetAsync("/api/tenant/current")).StatusCode);
            statuses.Add((await secondClient.GetAsync("/api/tenant/current")).StatusCode);
        }

        // Aynı firmanın iki kullanıcısı birlikte sayılır; başka firma etkilenmez.
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.OK, (await outsiderClient.GetAsync("/api/tenant/current")).StatusCode);
    }

    [Fact]
    public async Task Api_errors_without_a_body_carry_problem_details()
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync("/api/tenant/current");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_available(string path)
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, string email)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
