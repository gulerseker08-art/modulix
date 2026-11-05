using System.Net;
using System.Net.Http.Json;
using Modulix.Shared.Localization;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0034: arayüz metinleri uç noktası. Oturumsuz, yalnızca sabit ürün metni; dil tercihinden bağımsız çalışır.</summary>
public class TextBundleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Theory]
    [InlineData("tr", "Kaydet")]
    [InlineData("en", "Save")]
    [InlineData("az", "Yadda saxla")]
    public async Task Each_supported_language_is_served_without_a_session(string language, string save)
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync($"/api/i18n/{language}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bundle = (await response.Content.ReadFromJsonAsync<TextBundleResponse>())!;
        Assert.Equal(language, bundle.Language);
        Assert.Equal(save, bundle.Texts["common.save"]);
        Assert.Contains("max-age", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Every_language_serves_every_source_key()
    {
        using var client = factory.CreateBrowserClient();
        var source = (await client.GetFromJsonAsync<TextBundleResponse>("/api/i18n/tr"))!.Texts.Keys;

        foreach (var language in new[] { "en", "az" })
        {
            var texts = (await client.GetFromJsonAsync<TextBundleResponse>($"/api/i18n/{language}"))!.Texts;
            Assert.All(source, key => Assert.True(texts.ContainsKey(key), $"{language}: {key}"));
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("tr-TR")]
    [InlineData("x")]
    public async Task Unknown_language_is_404_and_nothing_else_is_served(string language)
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync($"/api/i18n/{language}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bundle_never_carries_tenant_or_user_data()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();

        var json = await client.GetStringAsync("/api/i18n/tr");

        Assert.DoesNotContain(tenant.Slug, json);
        Assert.DoesNotContain(user.Email!, json);
    }
}
