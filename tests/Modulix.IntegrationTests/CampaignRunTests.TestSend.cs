using System.Net;
using System.Net.Http.Json;
using Modulix.Modules.Marketing.Contracts;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Marketing;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 11: test gönderimi. Alıcı yalnızca çağıranın kendi hesap e-postasıdır (istekten alınmaz). Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu,
/// yetki. Test sağlayıcısı iletiyi hiçbir yere göndermez; sağlayıcıya giden istekler kaydedilir.
/// </summary>
public partial class CampaignRunTests
{
    private static IEnumerable<MessageRequest> TestMessages(string marker) => Sent.Where(r => r.Subject?.Contains(marker, StringComparison.Ordinal) == true && r.Subject.StartsWith("[TEST]", StringComparison.Ordinal));

    [Fact]
    public async Task A_test_send_goes_only_to_the_callers_own_address_with_sample_values_and_no_unsubscribe_link()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var marker = $"TS{Guid.NewGuid():N}"[..10];
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, $"{marker} {{{{firma|Firmanız}}}} teklifi", "Sayın {{ad_soyad|müşterimiz}}, merhaba. https://example.test/x");
        var me = (await w.Preparer.GetFromJsonAsync<MeResponse>("/api/auth/me"))!;

        var response = await w.Preparer.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TestSendResultDto>())!;
        Assert.Equal("Accepted", result.Outcome);

        var sent = Assert.Single(TestMessages(marker));
        Assert.Equal(me.Email.ToLowerInvariant(), sent.Address);
        Assert.Equal($"[TEST] {marker} Örnek A.Ş. teklifi", sent.Subject);
        Assert.Contains("Sayın Ayşe Yılmaz, merhaba.", sent.Body, StringComparison.Ordinal);
        Assert.Contains("Bu bir test iletisidir", sent.Body, StringComparison.Ordinal);
        Assert.Null(sent.Unsubscribe);
        Assert.DoesNotContain(Sent, r => r.Subject?.Contains(marker, StringComparison.Ordinal) == true && r.Address != me.Email.ToLowerInvariant());

        // Yanıt adres içermez; yedek (kampanya alıcılarına) hiçbir ileti gitmedi.
        Assert.DoesNotContain("@", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_test_send_is_refused_without_a_provider_for_unknown_content_and_beyond_the_daily_limit()
    {
        var w = await WorldAsync();
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, "Test sınırı", "Metin");

        // Sağlayıcı yok: gönderilmez.
        var blocked = await w.Preparer.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("sağlayıcı", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        Assert.Equal(HttpStatusCode.NotFound, (await w.Preparer.PostAsync($"/api/marketing/campaign-contents/{Guid.NewGuid()}/test-send", null)).StatusCode);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await w.Preparer.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null)).StatusCode);
        }

        var limited = await w.Preparer.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null);
        Assert.Equal(HttpStatusCode.Conflict, limited.StatusCode);
        Assert.Contains("sınırı", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_send_cannot_use_another_tenants_content()
    {
        var a = await WorldAsync();
        var b = await WorldAsync();
        await SetSettingAsync(b.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var content = await ContentAsync(a.Preparer, a.Campaign.Id, "A kiracısı konusu", "A metni");

        Assert.Equal(HttpStatusCode.NotFound, (await b.Preparer.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null)).StatusCode);
        Assert.DoesNotContain(Sent, r => r.Subject?.Contains("A kiracısı konusu", StringComparison.Ordinal) == true && r.Subject.StartsWith("[TEST]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Users_without_campaign_write_permission_cannot_send_tests()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var marker = $"TP{Guid.NewGuid():N}"[..10];
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, marker, "Metin");

        // Onaylayıcı içeriği okuyabilir ama yazma izni yoktur; test gönderemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Approver.PostAsync($"/api/marketing/campaign-contents/{content.Id}/test-send", null)).StatusCode);
        Assert.Empty(TestMessages(marker));
    }
}
