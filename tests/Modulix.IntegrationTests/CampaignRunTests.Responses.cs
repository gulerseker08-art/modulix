using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Communication;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

// ADR-0025 dilim 7: kampanya yanıtı (kayıt, üstlenme, lead ve aktivite açma, çıkış uygulama, kapatma), kampanya kaynak/UTM alanları. Dört test türü: mutlu yol, hata/doğrulama,
// tenant izolasyonu, yetki. Bu dosya CampaignRunTests'in dünya kurulumunu paylaşır.
public partial class CampaignRunTests
{
    private static readonly string[] Responder =
    [
        CampaignsRead, RecipientsRead, ResponsesRead, ResponsesWrite,
        CrmModule.Permissions.LeadsRead, CrmModule.Permissions.LeadsCreate, CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite,
    ];

    private sealed record Sent_(World World, HttpClient Responder, IReadOnlyList<CampaignDeliveryDto> Accepted, IReadOnlyList<CampaignDeliveryDto> Failed);

    /// <summary>Çalıştırma tamamlanmış dünya: iki ileti kabul edildi, biri belirsiz, ikisi hatalı. Yanıtlayıcı: yanıt, lead ve aktivite yetkili kullanıcı.</summary>
    private async Task<Sent_> SentWorldAsync()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);

        var accepted = (await w.Approver.GetFromJsonAsync<CampaignDeliveryPageDto>($"/api/marketing/campaign-runs/{run.Id}/deliveries?status=Accepted&pageSize=50"))!.Items;
        var failed = (await w.Approver.GetFromJsonAsync<CampaignDeliveryPageDto>($"/api/marketing/campaign-runs/{run.Id}/deliveries?status=Failed&pageSize=50"))!.Items;
        Assert.Equal(2, accepted.Count);
        return new Sent_(w, await UserClientAsync(w.Tenant, Responder), accepted, failed);
    }

    private static async Task<HttpResponseMessage> RegisterRawAsync(HttpClient client, Guid deliveryId, string kind, string? note = null, DateTimeOffset? receivedAt = null) =>
        await client.PostAsJsonAsync("/api/marketing/campaign-responses", new RegisterCampaignResponseRequest(deliveryId, kind, note, receivedAt));

    private static async Task<CampaignResponseDto> RegisterAsync(HttpClient client, Guid deliveryId, string kind, string? note = null)
    {
        var response = await RegisterRawAsync(client, deliveryId, kind, note);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CampaignResponseDto>())!;
    }

    private static Task<HttpResponseMessage> ResponseActionRawAsync(HttpClient client, Guid id, string action, string? reason = null, long? version = null) =>
        client.PostAsJsonAsync($"/api/marketing/campaign-responses/{id}/{action}", new ResponseActionRequest(version, reason));

    private static async Task<CampaignResponseDto> ResponseActionAsync(HttpClient client, Guid id, string action, string? reason = null)
    {
        var response = await ResponseActionRawAsync(client, id, action, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CampaignResponseDto>())!;
    }

    private static void AssertRejected(HttpResponseMessage response, HttpStatusCode expected) => Assert.Equal(expected, response.StatusCode);

    // ---- Mutlu yol ----

    [Fact]
    public async Task A_response_goes_from_registration_to_a_lead_and_a_follow_up_with_the_campaign_source()
    {
        var s = await SentWorldAsync();
        var delivery = s.Accepted[0];

        // Kayıt: yanıtı kaydeden sorumlu olur; kampanya, çalıştırma ve hedef iletiden gelir; yanıtta adres yoktur.
        var registered = await RegisterAsync(s.Responder, delivery.Id, "Interested", "Fiyat bilgisi istiyoruz");
        Assert.Equal(("New", s.World.Campaign.Id, delivery.TargetId), (registered.Status, registered.CampaignId, registered.TargetId));
        Assert.NotNull(registered.RunId);
        Assert.DoesNotContain("@", JsonSerializer.Serialize(registered));

        var taken = await ResponseActionAsync(s.Responder, registered.Id, "take");
        Assert.Equal("InProgress", taken.Status);

        // Lead: kampanya kaynak/UTM varsayılanlarıyla (kaynak "kampanya", ortam kanal, kampanya kodu); kişi ve firma bilgisi ileti adresinden çözülür.
        var converted = await ResponseActionAsync(s.Responder, registered.Id, "convert-to-lead");
        Assert.Equal("Converted", converted.Status);
        var lead = (await s.Responder.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{converted.LeadId}"))!;
        Assert.Equal(("kampanya", "email", s.World.Campaign.Code), (lead.UtmSource, lead.UtmMedium, lead.UtmCampaign));
        Assert.Equal($"Kampanya: {s.World.Campaign.Name}", lead.SourceDetail);
        // Kampanya bağı: yanıttan açılan müşteri adayı kampanyaya kayıt olarak da bağlanır (yanıtlayıcının kampanya okuma izni aranmaz).
        Assert.Equal(s.World.Campaign.Id, lead.CampaignId);
        Assert.Contains(lead.Email, new[] { "ok1@a.test", "ok2@b.test" });
        Assert.EndsWith("Tekstil", lead.CompanyName);
        Assert.Equal("Fiyat bilgisi istiyoruz", lead.Description);

        // İkinci dönüşüm ikinci lead açmaz.
        AssertRejected(await ResponseActionRawAsync(s.Responder, registered.Id, "convert-to-lead"), HttpStatusCode.Conflict);

        // Takip aktivitesi: lead'e bağlı açılır, yanıt "dönüştü" durumunda kalır; ikincisi açılmaz.
        var followed = await ResponseActionAsync(s.Responder, registered.Id, "create-activity");
        Assert.Equal(("Converted", true), (followed.Status, followed.ActivityId is not null));
        var activity = (await s.Responder.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{followed.ActivityId}"))!;
        Assert.StartsWith("Kampanya yanıtı", activity.Subject);
        AssertRejected(await ResponseActionRawAsync(s.Responder, registered.Id, "create-activity"), HttpStatusCode.Conflict);

        // Kapatma ve yeniden açma gerekçe ister.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ResponseActionRawAsync(s.Responder, registered.Id, "close")).StatusCode);
        Assert.Equal("Closed", (await ResponseActionAsync(s.Responder, registered.Id, "close", "Görüşüldü")).Status);
        var reopened = await ResponseActionAsync(s.Responder, registered.Id, "reopen", "Müşteri tekrar yazdı");
        Assert.Equal(("New", null), (reopened.Status, reopened.ClosedReason));
    }

    [Fact]
    public async Task Campaign_utm_values_are_normalized_and_flow_to_the_lead()
    {
        var s = await SentWorldAsync();
        var campaign = (await s.World.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{s.World.Campaign.Id}"))!;
        var saved = await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}",
            new SaveCampaignRequest(campaign.Version, campaign.Code, campaign.Name, campaign.Description, campaign.Purpose, campaign.Target, campaign.Channel, campaign.RecipientRule,
                campaign.RecipientRole, campaign.MaxPerTarget, campaign.StartsOn, campaign.EndsOn, campaign.OwnerId, "  Bulten ", "E-Bulten", "Yaz-2026"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updated = (await saved.Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(("bulten", "e-bulten", "yaz-2026"), (updated.UtmSource, updated.UtmMedium, updated.UtmCampaign));

        var registered = await RegisterAsync(s.Responder, s.Accepted[0].Id, "MeetingRequest");
        var lead = (await s.Responder.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{(await ResponseActionAsync(s.Responder, registered.Id, "convert-to-lead")).LeadId}"))!;
        Assert.Equal(("bulten", "e-bulten", "yaz-2026"), (lead.UtmSource, lead.UtmMedium, lead.UtmCampaign));
    }

    [Fact]
    public async Task An_opt_out_response_records_the_refusal_for_the_address_that_received_the_message_and_the_next_run_honours_it()
    {
        var s = await SentWorldAsync();
        var registered = await RegisterAsync(s.Responder, s.Accepted[0].Id, "OptOut", "STOP");

        // Çıkış lead'e dönüşmez; yalnızca "iletişim istemiyorum" yanıtından uygulanır.
        AssertRejected(await ResponseActionRawAsync(s.Responder, registered.Id, "convert-to-lead"), HttpStatusCode.Conflict);
        var applied = await ResponseActionAsync(s.Responder, registered.Id, "apply-opt-out");
        Assert.Equal(("Closed", "Çıkış uygulandı", true), (applied.Status, applied.ClosedReason, applied.OptOutAppliedAt is not null));
        AssertRejected(await ResponseActionRawAsync(s.Responder, registered.Id, "apply-opt-out"), HttpStatusCode.Conflict);

        // Tam olarak bir adres reddedildi (kaynak "abonelikten çıkma"); diğer alıcının izni durur.
        var preferences = (await s.World.Preparer.GetFromJsonAsync<PreferencePageDto>("/api/communication/preferences?pageSize=100"))!.Items
            .Where(p => p.Address is "ok1@a.test" or "ok2@b.test").ToList();
        Assert.Equal(1, preferences.Count(p => p.Status == "Denied"));
        Assert.Equal("unsubscribe", preferences.Single(p => p.Status == "Denied").Source);
        Assert.Equal(1, preferences.Count(p => p.Status == "Granted"));

        var second = await PreparedRunAsync(s.World);
        await ApproveAsync(s.World, second);
        await ActAsync(s.World.Approver, second.Id, "start");
        await ProcessAllAsync(s.World.Approver, second.Id);
        Assert.Equal(1, (await SummaryAsync(s.World.Approver, second.Id)).Codes["Skipped:denied"]);
    }

    // ---- Hata / doğrulama ----

    [Fact]
    public async Task Responses_are_validated_and_only_attach_to_messages_that_were_handed_to_the_provider()
    {
        var s = await SentWorldAsync();
        var delivery = s.Accepted[0];

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RegisterRawAsync(s.Responder, delivery.Id, "Bilinmeyen")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RegisterRawAsync(s.Responder, delivery.Id, "Question", new string('x', 2001))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RegisterRawAsync(s.Responder, delivery.Id, "Question", null, DateTimeOffset.UtcNow.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterRawAsync(s.Responder, Guid.NewGuid(), "Question")).StatusCode);

        // Hatalı (sağlayıcıya verilmemiş) iletiye yanıt kaydedilmez.
        Assert.NotEmpty(s.Failed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RegisterRawAsync(s.Responder, s.Failed[0].Id, "Question")).StatusCode);

        // Yanıtı olan kampanya silinemez.
        await RegisterAsync(s.Responder, delivery.Id, "Question");
        var campaign = (await s.World.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{s.World.Campaign.Id}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await s.World.Preparer.DeleteAsync($"/api/marketing/campaigns/{campaign.Id}?version={campaign.Version}")).StatusCode);
    }

    [Fact]
    public async Task Response_actions_follow_the_state_machine_and_utm_values_are_validated()
    {
        var s = await SentWorldAsync();
        var question = await RegisterAsync(s.Responder, s.Accepted[0].Id, "Question");
        var refusal = await RegisterAsync(s.Responder, s.Accepted[1].Id, "NotInterested");

        await ResponseActionAsync(s.Responder, question.Id, "take");
        AssertRejected(await ResponseActionRawAsync(s.Responder, question.Id, "take"), HttpStatusCode.Conflict);
        AssertRejected(await ResponseActionRawAsync(s.Responder, question.Id, "apply-opt-out"), HttpStatusCode.Conflict);
        AssertRejected(await ResponseActionRawAsync(s.Responder, question.Id, "reopen", "gerekçe"), HttpStatusCode.Conflict);

        // Ret ve şikâyet pazarlama fırsatı değildir.
        var rejected = await ResponseActionRawAsync(s.Responder, refusal.Id, "convert-to-lead");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("dönüştürülemez", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await ResponseActionRawAsync(s.Responder, question.Id, "bilinmeyen")).StatusCode);

        // Eski sürümle eylem 409.
        var stale = await ResponseActionRawAsync(s.Responder, refusal.Id, "close", "bitti", version: 99);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Geçersiz UTM değeri alan hatasıdır.
        var campaign = (await s.World.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{s.World.Campaign.Id}"))!;
        var invalid = await s.World.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}",
            new SaveCampaignRequest(campaign.Version, campaign.Code, campaign.Name, campaign.Description, campaign.Purpose, campaign.Target, campaign.Channel, campaign.RecipientRule,
                campaign.RecipientRole, campaign.MaxPerTarget, campaign.StartsOn, campaign.EndsOn, campaign.OwnerId, "iki kelime", null, "a&b"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("utmSource", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("utmCampaign", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_conversions_open_exactly_one_lead()
    {
        var s = await SentWorldAsync();
        var registered = await RegisterAsync(s.Responder, s.Accepted[0].Id, "Interested");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ResponseActionRawAsync(s.Responder, registered.Id, "convert-to-lead")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var final = (await s.Responder.GetFromJsonAsync<CampaignResponseDto>($"/api/marketing/campaign-responses/{registered.Id}"))!;
        Assert.NotNull(final.LeadId);
        var leads = (await s.Responder.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads?pageSize=100"))!.Items.Where(l => l.SourceDetail == $"Kampanya: {s.World.Campaign.Name}").ToList();
        Assert.Single(leads);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Another_tenant_cannot_see_register_or_act_on_responses_and_an_opt_out_stays_inside_its_tenant()
    {
        var a = await SentWorldAsync();
        var b = await SentWorldAsync();
        var response = await RegisterAsync(a.Responder, a.Accepted[0].Id, "OptOut");

        // B, A'nın iletisine yanıt kaydedemez; A'nın yanıtını okuyamaz, listeleyemez, eylem yapamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterRawAsync(b.Responder, a.Accepted[0].Id, "Question")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Responder.GetAsync($"/api/marketing/campaign-responses/{response.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ResponseActionRawAsync(b.Responder, response.Id, "close", "x")).StatusCode);
        Assert.Empty((await b.Responder.GetFromJsonAsync<PagedResponses>("/api/marketing/campaign-responses"))!.Items);

        // A'daki çıkış B'nin aynı adresteki iznini değiştirmez.
        await ResponseActionAsync(a.Responder, response.Id, "apply-opt-out");
        var bPreferences = (await b.World.Preparer.GetFromJsonAsync<PreferencePageDto>("/api/communication/preferences?pageSize=100"))!.Items
            .Where(p => p.Address is "ok1@a.test" or "ok2@b.test");
        Assert.All(bPreferences, p => Assert.Equal("Granted", p.Status));
    }

    // ---- Yetki ----

    [Fact]
    public async Task Each_response_operation_needs_its_own_permission_and_the_crm_permission_of_what_it_opens()
    {
        var s = await SentWorldAsync();
        var reader = await UserClientAsync(s.World.Tenant, [CampaignsRead, ResponsesRead]);
        var writerOnly = await UserClientAsync(s.World.Tenant, [CampaignsRead, ResponsesRead, ResponsesWrite, RecipientsRead]);
        var noRead = await UserClientAsync(s.World.Tenant, [CampaignsRead, ResponsesWrite]);
        var response = await RegisterAsync(s.Responder, s.Accepted[0].Id, "Interested");

        // Yazma izni olmayan kaydedemez ve eylem yapamaz; okuma izni olmayan listeleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterRawAsync(reader, s.Accepted[1].Id, "Question")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResponseActionRawAsync(reader, response.Id, "take")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRead.GetAsync("/api/marketing/campaign-responses")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/marketing/campaign-responses")).StatusCode);

        // Lead açma izni olmayan yanıttan lead açamaz (marketing izni CRM yetkisi vermez); yanıt değişmez, lead oluşmaz.
        var denied = await ResponseActionRawAsync(writerOnly, response.Id, "convert-to-lead");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var unchanged = (await s.Responder.GetFromJsonAsync<CampaignResponseDto>($"/api/marketing/campaign-responses/{response.Id}"))!;
        Assert.Equal(("New", null), (unchanged.Status, unchanged.LeadId));

        // Genel yazma uçları yoktur: yanıt, durum ve lead alanları istemciden yazılamaz.
        Assert.True((await s.Responder.PutAsJsonAsync($"/api/marketing/campaign-responses/{response.Id}", new { status = "Closed" })).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True((await s.Responder.DeleteAsync($"/api/marketing/campaign-responses/{response.Id}")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        using var anonymous = host.CreateClient(Browser());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/marketing/campaign-responses")).StatusCode);

        // Modül kapanınca yanıt uçları 403; veri silinmez.
        await factory.SetModuleAsync(s.World.Tenant.Id, MarketingModule.ModuleKey, enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Responder.GetAsync("/api/marketing/campaign-responses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResponseActionRawAsync(s.Responder, response.Id, "take")).StatusCode);
        await factory.SetModuleAsync(s.World.Tenant.Id, MarketingModule.ModuleKey, enabled: true);
        Assert.Equal("New", (await s.Responder.GetFromJsonAsync<CampaignResponseDto>($"/api/marketing/campaign-responses/{response.Id}"))!.Status);
    }

    [Fact]
    public async Task A_user_with_own_scope_only_sees_the_responses_they_are_responsible_for()
    {
        var s = await SentWorldAsync();
        var scoped = await factory.CreateUserAsync(s.World.Tenant);
        await factory.CreateRoleAsync(s.World.Tenant.Id, Responder, RecordScope.Own, scoped);
        var scopedClient = await LoginAsync(scoped);
        var response = await RegisterAsync(s.Responder, s.Accepted[0].Id, "Question");

        // Yanıtın sorumlusu başka biri: dar kapsamlı kullanıcı onu göremez, listeleyemez, eylem yapamaz. Başkasının kampanyasının iletisi de ona "yok" gibi görünür.
        Assert.Equal(HttpStatusCode.NotFound, (await scopedClient.GetAsync($"/api/marketing/campaign-responses/{response.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ResponseActionRawAsync(scopedClient, response.Id, "take")).StatusCode);
        Assert.Empty((await scopedClient.GetFromJsonAsync<PagedResponses>("/api/marketing/campaign-responses"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterRawAsync(scopedClient, s.Accepted[1].Id, "Question")).StatusCode);

        // Geniş kapsamlı kullanıcı görür; üstlenince sorumlu o olur.
        Assert.Equal(response.Id, Assert.Single((await s.Responder.GetFromJsonAsync<PagedResponses>("/api/marketing/campaign-responses"))!.Items).Id);
    }

    private sealed record PagedResponses(IReadOnlyList<CampaignResponseDto> Items, int Page, int PageSize, int TotalCount);
}
