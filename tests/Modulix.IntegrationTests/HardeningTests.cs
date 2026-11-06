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
    }
}
