using System.Net;
using System.Net.Http.Json;
using Modulix.Modules.Marketing;
using Modulix.Shared.Communication;
using Modulix.Shared.Marketing;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 14: kampanyanın iletişim konusu. Konudan çıkmış alıcı hedef kitle dondurulurken elenir ve gönderimden hemen önce yeniden denetlenir; konu izin vermez.
/// Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki. Test sağlayıcısı iletiyi hiçbir yere göndermez.
/// </summary>
public partial class CampaignRunTests
{
    private const string TopicKey = "haber-bulteni";

    private static async Task DefineTopicAsync(HttpClient client, string key = TopicKey, bool isActive = true)
    {
        var response = await client.PutAsJsonAsync("/api/communication/topics", new SaveTopicRequest(key, "Haber bülteni", isActive));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task OptOutOfTopicAsync(HttpClient client, string address, string key = TopicKey)
    {
        var response = await client.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", address, key, true, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Kampanyayı güncel sürümüyle okuyup yalnızca konusunu değiştirerek kaydeder (PUT her alanı yazar).</summary>
    private static async Task<HttpResponseMessage> SetCampaignTopicAsync(HttpClient client, Guid campaignId, string? topic, string? name = null)
    {
        var c = (await client.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{campaignId}"))!;
        return await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaignId}", new SaveCampaignRequest(
            c.Version, c.Code, name ?? c.Name, c.Description, c.Purpose, c.Target, c.Channel, c.RecipientRule, c.RecipientRole, c.MaxPerTarget, c.StartsOn, c.EndsOn, c.OwnerId,
            c.UtmSource, c.UtmMedium, c.UtmCampaign, c.Budget, c.ActualCost, c.CostCurrency, topic));
    }

    private static async Task<AudienceSnapshotDto> FreezeAsync(HttpClient client, Guid campaignId)
    {
        var response = await client.PostAsync($"/api/marketing/campaigns/{campaignId}/audience/snapshot", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;
    }

    private async Task<CampaignRunDto> StartedRunAsync(World w, Guid snapshotId)
    {
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var created = await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs",
            new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: snapshotId, ContentId: w.Content.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var run = (await created.Content.ReadFromJsonAsync<CampaignRunDto>())!;
        await ActAsync(w.Preparer, run.Id, "submit");
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);
        return run;
    }

    [Fact]
    public async Task A_recipient_who_left_the_campaigns_topic_is_excluded_at_freeze_and_skipped_at_send_time()
    {
        var w = await WorldAsync();
        await DefineTopicAsync(w.Preparer);
        await OptOutOfTopicAsync(w.Preparer, "ok1@a.test");
        var key = w.Tenant.Id.ToString("N");

        // Konusuz kampanya konu tercihini yok sayar: dondurulan kitle aynı kalır.
        Assert.Equal(5, (await FreezeAsync(w.Preparer, w.Campaign.Id)).EligibleCount);

        // Konu seçilince yeni dondurma o alıcıyı eler ve nedeni "topic-opt-out" olarak sayar.
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, TopicKey)).StatusCode);
        var narrowed = await FreezeAsync(w.Preparer, w.Campaign.Id);
        Assert.Equal(4, narrowed.EligibleCount);
        Assert.Contains("\"topic-opt-out\":1", narrowed.Exclusions.Replace(" ", string.Empty), StringComparison.Ordinal);

        // Konudan çıkış kitle dondurulduktan sonra olsa da gönderimden hemen önce yeniden denetlenir: eski (5 uygun) anlık görüntüyle çalıştırma o alıcıyı atlar.
        var run = await StartedRunAsync(w, w.Snapshot.Id);
        var summary = await SummaryAsync(w.Approver, run.Id);
        Assert.Equal(1, summary.Codes["Skipped:topic-opt-out"]);
        var sentToMe = Sent.Where(r => r.Unsubscribe?.PageUrl.Contains(key, StringComparison.Ordinal) == true).Select(r => r.Address).ToList();
        Assert.DoesNotContain("ok1@a.test", sentToMe);
        Assert.Contains("ok2@b.test", sentToMe);
    }

    [Fact]
    public async Task A_topic_does_not_grant_consent_and_a_campaign_without_a_topic_ignores_topic_opt_outs()
    {
        var w = await WorldAsync();
        await DefineTopicAsync(w.Preparer);

        // İzni olmayan alıcı konuya dönse de gönderilmez: konu izin vermez (konu tercihi kaydı "konuda" olsa bile karar "bilinmiyor" kalır).
        var company = await CompanyAsync(w.Preparer, "Fatma Tekstil");
        await ContactAsync(w.Preparer, company.Id, "Fatma", "Kişi", "izinsiz@f.test");
        var back = await w.Preparer.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", "izinsiz@f.test", TopicKey, false, null));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, TopicKey)).StatusCode);
        var snapshot = await FreezeAsync(w.Preparer, w.Campaign.Id);
        Assert.Equal(5, snapshot.EligibleCount);   // ok1, ok2 ve üç hata senaryosu; izinsiz firma kitleye girmez
        Assert.Contains("\"unknown\":1", snapshot.Exclusions.Replace(" ", string.Empty), StringComparison.Ordinal);

        // Konu kaldırılınca (boş) konu tercihleri uygulanmaz.
        await OptOutOfTopicAsync(w.Preparer, "ok2@b.test");
        Assert.Equal(4, (await FreezeAsync(w.Preparer, w.Campaign.Id)).EligibleCount);
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, null)).StatusCode);
        Assert.Equal(5, (await FreezeAsync(w.Preparer, w.Campaign.Id)).EligibleCount);
    }

    [Fact]
    public async Task A_campaign_topic_must_be_defined_and_open_but_a_topic_closed_later_does_not_block_editing()
    {
        var w = await WorldAsync();

        // Tanımsız konu reddedilir; kampanya değişmez.
        var unknown = await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, "yok-boyle-konu");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("topic", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null((await w.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{w.Campaign.Id}"))!.Topic);

        // Biçimi bozuk anahtar da reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, "Bozuk Anahtar")).StatusCode);

        // Kapalı konu seçilemez; açıkken seçilir; sonradan kapanırsa kampanya konuyu değiştirmeden düzenlenebilir ama başka kampanyaya yeni atanamaz.
        await DefineTopicAsync(w.Preparer, isActive: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, TopicKey)).StatusCode);
        await DefineTopicAsync(w.Preparer, isActive: true);
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, TopicKey)).StatusCode);
        await DefineTopicAsync(w.Preparer, isActive: false);
        var renamed = await SetCampaignTopicAsync(w.Preparer, w.Campaign.Id, TopicKey, name: "Yeniden adlandırıldı");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var updated = (await renamed.Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal((TopicKey, "Yeniden adlandırıldı"), (updated.Topic, updated.Name));

        // Kapalı konudaki çıkışlar korunur: gönderimde yine uygulanır.
        await OptOutOfTopicAsync(w.Preparer, "ok1@a.test");
        Assert.Equal(4, (await FreezeAsync(w.Preparer, w.Campaign.Id)).EligibleCount);
    }

    [Fact]
    public async Task Topics_and_opt_outs_of_another_tenant_never_apply_to_this_tenants_campaign()
    {
        var a = await WorldAsync();
        var b = await WorldAsync();
        await DefineTopicAsync(a.Preparer);
        await OptOutOfTopicAsync(a.Preparer, "ok1@a.test");

        // B'de konu tanımlı değilken A'nın anahtarı B'nin kampanyasında kullanılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCampaignTopicAsync(b.Preparer, b.Campaign.Id, TopicKey)).StatusCode);

        // B aynı anahtarı tanımlasa da A'daki çıkış B'nin alıcısını (aynı adres) elemez.
        await DefineTopicAsync(b.Preparer);
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(b.Preparer, b.Campaign.Id, TopicKey)).StatusCode);
        Assert.Equal(5, (await FreezeAsync(b.Preparer, b.Campaign.Id)).EligibleCount);

        // A'nın kampanyası kendi çıkışını uygular.
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(a.Preparer, a.Campaign.Id, TopicKey)).StatusCode);
        Assert.Equal(4, (await FreezeAsync(a.Preparer, a.Campaign.Id)).EligibleCount);
    }

    [Fact]
    public async Task Changing_a_campaigns_topic_needs_campaign_write_permission()
    {
        var w = await WorldAsync();
        await DefineTopicAsync(w.Preparer);

        // Onaylayıcının kampanya yazma izni yoktur: konu değiştiremez, kampanya değişmez.
        var denied = await SetCampaignTopicAsync(w.Approver, w.Campaign.Id, TopicKey);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null((await w.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{w.Campaign.Id}"))!.Topic);

        // Konu tanımlama çekirdeğin iletişim yönetimi iznidir: kampanya yazma izni tek başına yetmez.
        var marketingOnly = await UserClientAsync(w.Tenant, [CampaignsRead, CampaignsWrite]);
        Assert.Equal(HttpStatusCode.Forbidden, (await marketingOnly.PutAsJsonAsync("/api/communication/topics", new SaveTopicRequest("yeni-konu", "Yeni", true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetCampaignTopicAsync(marketingOnly, w.Campaign.Id, TopicKey)).StatusCode);
        Assert.Equal(TopicKey, (await w.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{w.Campaign.Id}"))!.Topic);
    }
}
