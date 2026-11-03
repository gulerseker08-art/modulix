using System.Net;
using System.Net.Http.Json;
using Modulix.Modules.Marketing;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 11: kişiye özel değişkenler. Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki. Test sağlayıcısı iletiyi hiçbir yere göndermez;
/// sağlayıcıya giden istekler kaydedilir.
/// </summary>
public partial class CampaignRunTests
{
    private const string TemplateSubject = "{{firma|Firmanız}} için özel teklif";
    private const string TemplateBody = "Sayın {{ad_soyad|müşterimiz}}, {{firma|firmanız}} için hazırladığımız teklif: https://example.test/teklif";

    private async Task<CampaignRunDto> StartedTemplateRunAsync(World w)
    {
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, TemplateSubject, TemplateBody);
        var created = await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs",
            new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: w.Snapshot.Id, ContentId: content.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var run = (await created.Content.ReadFromJsonAsync<CampaignRunDto>())!;
        await ActAsync(w.Preparer, run.Id, "submit");
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);
        return run;
    }

    [Fact]
    public async Task Each_recipient_gets_their_own_values_and_the_stored_content_stays_a_template()
    {
        var w = await WorldAsync();
        var run = await StartedTemplateRunAsync(w);

        var key = w.Tenant.Id.ToString("N");
        var mine = Sent.Where(r => r.Unsubscribe?.PageUrl.Contains(key, StringComparison.Ordinal) == true).ToList();
        var anka = Assert.Single(mine, r => r.Address == "ok1@a.test");
        var bora = Assert.Single(mine, r => r.Address == "ok2@b.test");

        // Kişi adı kişinin kendi kaydından, firma adı firmadan gelir; her alıcının iletisi farklıdır.
        Assert.Equal("Anka Tekstil için özel teklif", anka.Subject);
        Assert.StartsWith("Sayın Anka Kişi, Anka Tekstil için hazırladığımız teklif: https://example.test/teklif", anka.Body, StringComparison.Ordinal);
        Assert.Equal("Bora Tekstil için özel teklif", bora.Subject);
        Assert.StartsWith("Sayın Bora Kişi, Bora Tekstil için", bora.Body, StringComparison.Ordinal);

        // Alt bilgi doldurmadan sonra eklenir ve alıcıya özgüdür; saklanan içerik şablon olarak kalır (onaylanan metin değişmez).
        Assert.Contains("abonelik/", anka.Body, StringComparison.Ordinal);
        Assert.NotEqual(anka.Unsubscribe!.PageUrl, bora.Unsubscribe!.PageUrl);
        var stored = (await w.Preparer.GetFromJsonAsync<CampaignContentDto>($"/api/marketing/campaign-contents/{(await w.Preparer.GetFromJsonAsync<CampaignRunDto>($"/api/marketing/campaign-runs/{run.Id}"))!.ContentId}"))!;
        Assert.Equal(TemplateBody, stored.Body);
        Assert.DoesNotContain("{{", anka.Body + anka.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_template_without_a_fallback_or_with_an_unknown_variable_is_rejected()
    {
        var w = await WorldAsync();
        foreach (var (subject, body) in new[]
        {
            ("Merhaba", "Sayın {{ad}}, teklifimiz"),
            ("Merhaba {{ad|x}}", "Sayın {{telefon|müşterimiz}}"),
            ("{{ad|x", "gövde"),
        })
        {
            var response = await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-contents", new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: subject, Body: body));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // Geçerli yedek metinle kaydedilir.
        Assert.Equal(HttpStatusCode.Created, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-contents",
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: "Merhaba {{ad|dostum}}", Body: "Sayın {{ad|dostum}}"))).StatusCode);
    }

    [Fact]
    public async Task Values_come_from_the_sending_tenants_own_records()
    {
        var a = await WorldAsync();
        var b = await WorldAsync();

        // B kiracısında aynı adlı firma yeniden adlandırılır; A'nın iletileri etkilenmez, B'ninki B'nin adını taşır.
        var companies = (await b.Preparer.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Anka&pageSize=10"))!.Items;
        var company = Assert.Single(companies);
        Assert.Equal(HttpStatusCode.OK, (await b.Preparer.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCompanyRequest("B Grubu Holding", Version: company.Version))).StatusCode);

        await StartedTemplateRunAsync(a);
        await StartedTemplateRunAsync(b);

        var ofA = Sent.Where(r => r.Unsubscribe?.PageUrl.Contains(a.Tenant.Id.ToString("N"), StringComparison.Ordinal) == true && r.Address == "ok1@a.test").ToList();
        var ofB = Sent.Where(r => r.Unsubscribe?.PageUrl.Contains(b.Tenant.Id.ToString("N"), StringComparison.Ordinal) == true && r.Address == "ok1@a.test").ToList();
        Assert.Equal("Anka Tekstil için özel teklif", Assert.Single(ofA).Subject);
        Assert.Equal("B Grubu Holding için özel teklif", Assert.Single(ofB).Subject);
    }

    [Fact]
    public async Task Only_content_writers_can_create_templates_and_the_approver_cannot_change_the_text()
    {
        var w = await WorldAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Approver.PostAsJsonAsync("/api/marketing/campaign-contents",
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: TemplateSubject, Body: TemplateBody))).StatusCode);

        // İçerik sürümü değişmez: şablon onaylandıktan sonra düzenlenemez.
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, TemplateSubject, TemplateBody);
        var run = (await (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs",
            new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: w.Snapshot.Id, ContentId: content.Id))).Content.ReadFromJsonAsync<CampaignRunDto>())!;
        await ActAsync(w.Preparer, run.Id, "submit");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Preparer.PutAsJsonAsync($"/api/marketing/campaign-contents/{content.Id}",
            new SaveCampaignContentRequest(content.Version, w.Campaign.Id, "{{ad|x}}", "Başka metin"))).StatusCode);
    }
}
