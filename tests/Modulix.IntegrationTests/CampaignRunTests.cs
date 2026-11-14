using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Modules.Marketing.Contracts;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Communication;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 5: içerik sürümü, onay (görev ayrılığı), başlatma, işleme, gönderim öncesi yeniden denetim, sıklık sınırı ve belirsiz sonuç. Dört test türü: mutlu yol,
/// hata/doğrulama, tenant izolasyonu, yetki. Test sağlayıcısı iletiyi hiçbir yere göndermez; bu testlerde gerçek ileti gitmez.
/// </summary>
public partial class CampaignRunTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<MessageRequest> Sent = [];

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public CampaignRunTests(ModulixApiFactory factory)
    {
        this.factory = factory;

        // Abonelikten çıkma bağlantısı genel adresle kurulur (adres yoksa pazarlama gönderimi başlamaz); bu testlerde adres ayarlıdır. Test sağlayıcısı
        // sarmalanır: aynı sonuçları verir, ek olarak sağlayıcıya giden istekleri kaydeder (hiçbir yere ileti gitmez).
        host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("App:PublicUrl", "https://app.modulix.test");
            builder.ConfigureServices(services =>
            {
                var fake = services.Single(d => d.ServiceType == typeof(IMessageProvider));
                services.Remove(fake);
                services.AddScoped<IMessageProvider>(sp => new RecordingProvider((IMessageProvider)ActivatorUtilities.CreateInstance(sp, fake.ImplementationType!), Sent));
            });
        });
    }

    private sealed class RecordingProvider(IMessageProvider inner, System.Collections.Concurrent.ConcurrentBag<MessageRequest> sent) : IMessageProvider
    {
        public string Key => inner.Key;

        public bool Supports(CommunicationChannel channel) => inner.Supports(channel);

        public Task<ProviderResult> SendAsync(MessageRequest request, CancellationToken cancellationToken)
        {
            sent.Add(request);
            return inner.SendAsync(request, cancellationToken);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    private static readonly string[] CrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite, CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
    ];

    private static readonly string[] Communication =
        [CoreModule.Permissions.ReadCommunication, CoreModule.Permissions.ManageCommunication, CoreModule.Permissions.SuppressCommunication];

    private static readonly string[] Preparer =
        [SegmentsRead, SegmentsWrite, SegmentsPublish, RecipientsRead, CampaignsRead, CampaignsWrite, CampaignsAudience, CoreModule.Permissions.ManageSettings, .. CrmPermissions, .. Communication];

    private static readonly string[] Approver = [CampaignsRead, CampaignsApprove, CampaignsSend, RecipientsRead];

    private const string Body = "Antrepo hizmetimiz hakkında bilgi: https://example.test/antrepo";

    // ---- Mutlu yol ----

    [Fact]
    public async Task A_run_goes_through_approval_start_and_processing_and_results_are_counted_honestly()
    {
        var w = await WorldAsync();
        var run = await PreparedRunAsync(w);

        // Sağlayıcı yokken gerçek gönderim bloklu: başlatma reddedilir, hiçbir teslimat satırı açılmaz.
        await ApproveAsync(w, run);
        var blocked = await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest());
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("sağlayıcı", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await SummaryAsync(w.Approver, run.Id)).Total);

        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        Assert.Equal("Running", (await ActAsync(w.Approver, run.Id, "start")).Status);
        var summary = await SummaryAsync(w.Approver, run.Id);
        Assert.Equal((5, true, "fake"), (summary.Total, summary.ProviderConfigured, summary.Provider));
        Assert.Equal(5, summary.Statuses["Queued"]);

        var final = await ProcessAllAsync(w.Approver, run.Id);
        Assert.True(final.Completed);
        summary = await SummaryAsync(w.Approver, run.Id);

        // İki alıcı sağlayıcıya kabul edildi ("teslim edildi" değil); biri kalıcı hata, biri geçici hatayı tüketti, biri belirsiz kaldı.
        Assert.Equal(2, summary.Statuses["Accepted"]);
        Assert.Equal(2, summary.Statuses["Failed"]);
        Assert.Equal(1, summary.Statuses["Unknown"]);
        Assert.Equal("Completed", summary.Status);
        Assert.Equal(1, summary.Codes["Failed:address-rejected"]);
        Assert.Equal(1, summary.Codes["Failed:rate-limited"]);
        Assert.Equal(1, summary.Codes["Unknown:timeout-after-accept"]);

        // Satırlarda adres yoktur; sağlayıcı kimliği yalnızca kabul edilenlerdedir.
        var raw = await w.Approver.GetStringAsync($"/api/marketing/campaign-runs/{run.Id}/deliveries?pageSize=50");
        Assert.DoesNotContain("@", raw);
        var accepted = (await w.Approver.GetFromJsonAsync<CampaignDeliveryPageDto>($"/api/marketing/campaign-runs/{run.Id}/deliveries?status=Accepted"))!;
        Assert.All(accepted.Items, d => Assert.StartsWith("fake-", d.ProviderMessageId));

        // Tamamlanan çalıştırma yeniden işlenmez, yeniden başlatılmaz; belirsiz satır kör yeniden denenmez.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest())).StatusCode);
        var unknown = (await w.Approver.GetFromJsonAsync<CampaignDeliveryPageDto>($"/api/marketing/campaign-runs/{run.Id}/deliveries?status=Unknown"))!;
        Assert.Equal(1, Assert.Single(unknown.Items).Attempts);
    }

    [Fact]
    public async Task Consent_and_suppression_are_rechecked_right_before_sending_and_the_snapshot_does_not_bypass_them()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);

        // Anlık görüntü alındıktan sonra bir kişi izni geri çeker, bir adres engellenir.
        await ConsentAsync(w.Preparer, "ok1@a.test", "Denied");
        Assert.Equal(HttpStatusCode.OK, (await w.Preparer.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", "ok2@b.test", "OptOut", null, null))).StatusCode);

        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);
        var summary = await SummaryAsync(w.Approver, run.Id);

        Assert.Equal(0, summary.Statuses.GetValueOrDefault("Accepted"));
        Assert.Equal(1, summary.Codes["Skipped:denied"]);
        Assert.Equal(1, summary.Codes["Skipped:suppressed:opt-out"]);
        Assert.Equal(2, summary.Statuses["Skipped"]);
    }

    [Fact]
    public async Task Deactivated_contact_points_are_skipped_as_address_gone()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);

        // Anlık görüntüden sonra bir kişinin iletişim noktası pasife alınır: eski kimlikle yeni bir adrese gönderilmez.
        var points = (await w.Preparer.GetFromJsonAsync<CrmPage<PersonContactPointDto>>("/api/crm/person-contact-points?pageSize=100"))!.Items;
        var point = points.First(p => p.Value == "ok1@a.test");
        Assert.Equal(HttpStatusCode.OK, (await w.Preparer.PutAsJsonAsync($"/api/crm/person-contact-points/{point.Id}",
            new SavePersonContactPointRequest(point.ContactId, point.Value, "Email", IsActive: false, Version: point.Version))).StatusCode);

        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);
        Assert.Equal(1, (await SummaryAsync(w.Approver, run.Id)).Codes["Skipped:address-gone"]);
    }

    [Fact]
    public async Task The_daily_frequency_limit_is_shared_across_runs()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var first = await PreparedRunAsync(w);
        await ApproveAsync(w, first);
        await ActAsync(w.Approver, first.Id, "start");
        await ProcessAllAsync(w.Approver, first.Id);
        Assert.Equal(2, (await SummaryAsync(w.Approver, first.Id)).Statuses["Accepted"]);

        // Aynı adresler aynı gün ikinci kez: sınır (günde 1) kampanyalar arasında ortaktır.
        var second = await PreparedRunAsync(w);
        await ApproveAsync(w, second);
        await ActAsync(w.Approver, second.Id, "start");
        await ProcessAllAsync(w.Approver, second.Id);
        var summary = await SummaryAsync(w.Approver, second.Id);
        Assert.Equal(0, summary.Statuses.GetValueOrDefault("Accepted"));
        Assert.Equal(3, summary.Codes["Skipped:frequency-limit"]);   // A, B ve sonucu belirsiz kalan E (hak korunur)

        // Sınır 2'ye çıkarılırsa yeni çalıştırma yeniden gönderebilir.
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.DailyLimit, "2");
        var third = await PreparedRunAsync(w);
        await ApproveAsync(w, third);
        await ActAsync(w.Approver, third.Id, "start");
        await ProcessAllAsync(w.Approver, third.Id);
        Assert.Equal(2, (await SummaryAsync(w.Approver, third.Id)).Statuses["Accepted"]);
    }

    [Fact]
    public async Task Concurrent_reservations_never_exceed_the_limit()
    {
        var tenant = await factory.CreateTenantAsync();
        var day = new DateOnly(2026, 11, 2);

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            using var scope = factory.OpenScope(tenant.Id);
            return await scope.ServiceProvider.GetRequiredService<IFrequencyLimiter>().TryReserveAsync(CommunicationChannel.Email, "yaris@x.test", day, 3, CancellationToken.None);
        }));

        Assert.Equal(3, results.Count(r => r));

        // Hak geri verilirse yeniden ayrılabilir; başka gün ve başka adres ayrı sayılır.
        using var scope = factory.OpenScope(tenant.Id);
        var limiter = scope.ServiceProvider.GetRequiredService<IFrequencyLimiter>();
        Assert.False(await limiter.TryReserveAsync(CommunicationChannel.Email, "yaris@x.test", day, 3, CancellationToken.None));
        await limiter.ReleaseAsync(CommunicationChannel.Email, "yaris@x.test", day, CancellationToken.None);
        Assert.True(await limiter.TryReserveAsync(CommunicationChannel.Email, "yaris@x.test", day, 3, CancellationToken.None));
        Assert.True(await limiter.TryReserveAsync(CommunicationChannel.Email, "yaris@x.test", day.AddDays(1), 3, CancellationToken.None));
        Assert.True(await limiter.TryReserveAsync(CommunicationChannel.Email, "baska@x.test", day, 3, CancellationToken.None));
    }

    [Fact]
    public async Task Pause_and_cancel_stop_new_work_and_never_pretend_that_accepted_messages_were_recalled()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");

        var first = (await (await w.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process?batchSize=2", null)).Content.ReadFromJsonAsync<ProcessResultDto>())!;
        Assert.Equal(2, first.Processed);

        Assert.Equal("Paused", (await ActAsync(w.Approver, run.Id, "pause")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);
        // Sıra, veritabanındaki kimlik sırasına göredir (garanti edilen bir iş sırası yoktur): sırada kalan = toplam - işlenen kalıcı sonuçlar.
        Assert.Equal(5 - first.Accepted - first.Failed - first.Skipped - first.Unknown, (await SummaryAsync(w.Approver, run.Id)).Statuses["Queued"]);

        Assert.Equal("Running", (await ActAsync(w.Approver, run.Id, "resume")).Status);
        await w.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process?batchSize=1", null);

        // İptalden sonra sırada kalanlar iptal edilir; kabul edilmiş iletiler "geri alındı" gösterilmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/cancel", new RunActionRequest())).StatusCode);
        var accepted = (await SummaryAsync(w.Approver, run.Id)).Statuses.GetValueOrDefault("Accepted");
        Assert.Equal("Cancelled", (await ActAsync(w.Approver, run.Id, "cancel", "Müşteri talebi")).Status);
        var summary = await SummaryAsync(w.Approver, run.Id);
        Assert.Equal(accepted, summary.Statuses.GetValueOrDefault("Accepted"));
        Assert.Equal(0, summary.Statuses.GetValueOrDefault("Queued"));
        Assert.True(summary.Statuses["Cancelled"] > 0);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);
    }

    // ---- Hata ve doğrulama ----

    [Fact]
    public async Task Approval_rules_separate_duties_and_any_change_after_submission_needs_a_new_approval()
    {
        var w = await WorldAsync();
        var run = await DraftRunAsync(w);

        // Hazır olmayan çalıştırma onaya gönderilemez: alanlar nedeniyle işaretlenir.
        var empty = (await (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id))).Content.ReadFromJsonAsync<CampaignRunDto>())!;
        var notReady = await w.Preparer.PostAsJsonAsync($"/api/marketing/campaign-runs/{empty.Id}/submit", new RunActionRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notReady.StatusCode);
        var errors = await notReady.Content.ReadAsStringAsync();
        Assert.Contains("snapshotId", errors);
        Assert.Contains("contentId", errors);

        Assert.Equal("PendingApproval", (await ActAsync(w.Preparer, run.Id, "submit")).Status);

        // Gönderilen çalıştırma düzenlenemez; hazırlayan kendi çalıştırmasını onaylayamaz (yetkisi olsa bile); onay yetkisi olmayan onaylayamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Preparer.PutAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}", Save(run) with { ContentId = null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Preparer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/approve", new RunActionRequest())).StatusCode);
        Assert.Equal("PendingApproval", (await ActAsync(w.Preparer, run.Id, "submit", expectStatus: HttpStatusCode.Conflict)).Status);
        var dual = await UserClientAsync(w.Tenant, [.. Preparer, CampaignsApprove]);
        var second = await DraftRunAsync(w);
        await ActAsync(dual, second.Id, "submit");
        Assert.Equal(HttpStatusCode.Forbidden, (await dual.PostAsJsonAsync($"/api/marketing/campaign-runs/{second.Id}/approve", new RunActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/approve", new RunActionRequest())).StatusCode);

        // Onaydan sonra içerik değişikliği: önce taslağa alınır (gerekçeyle), yeni sürüm bağlanır, onay yeniden alınır.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/reopen", new RunActionRequest())).StatusCode);
        var reopened = await ActAsync(w.Preparer, run.Id, "reopen", "Metin düzeltildi");
        Assert.Equal(("Draft", null), (reopened.Status, reopened.ApprovedBy?.ToString()));
        Assert.Null(reopened.SubmittedBy);
        var v2 = await ContentAsync(w.Preparer, w.Campaign.Id, "Merhaba (düzeltilmiş)", Body);
        Assert.Equal(2, v2.VersionNo);
        Assert.Equal(HttpStatusCode.OK, (await w.Preparer.PutAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}", Save(reopened) with { ContentId = v2.Id })).StatusCode);
        Assert.Equal("PendingApproval", (await ActAsync(w.Preparer, run.Id, "submit")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest())).StatusCode);

        // Ret gerekçe ister ve taslağa döndürür.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/reject", new RunActionRequest())).StatusCode);
        Assert.Equal("Draft", (await ActAsync(w.Approver, run.Id, "reject", "Konu uygun değil")).Status);
    }

    [Fact]
    public async Task Approval_can_be_turned_off_and_then_submission_is_directly_approved()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.ApprovalRequired, "false");
        var run = await DraftRunAsync(w);
        Assert.Equal("Approved", (await ActAsync(w.Preparer, run.Id, "submit")).Status);
    }

    [Fact]
    public async Task Content_and_run_input_rules_are_enforced()
    {
        var w = await WorldAsync();
        var bad = new[]
        {
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: null, Body: Body),                              // e-postada konu zorunlu
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: "Konu", Body: ""),                              // metin zorunlu
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: "İki\nsatır", Body: Body),                       // konu tek satır
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: "Konu", Body: "Tıklayın: javascript:alert(1)"),  // betik bağlantısı
            new SaveCampaignContentRequest(CampaignId: w.Campaign.Id, Subject: "Konu", Body: "Dosya: data:text/html;base64,AAA"),
        };
        foreach (var request in bad)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-contents", request)).StatusCode);
        }

        // İçerik sürümü değişmez ve silinemez.
        var content = await ContentAsync(w.Preparer, w.Campaign.Id, "Konu", Body);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Preparer.PutAsJsonAsync($"/api/marketing/campaign-contents/{content.Id}", new SaveCampaignContentRequest(content.Version, w.Campaign.Id, "Başka", Body))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Preparer.DeleteAsync($"/api/marketing/campaign-contents/{content.Id}?version={content.Version}")).StatusCode);

        // Çalıştırma: başka kampanyanın anlık görüntüsü/içeriği, geçmiş zaman, olmayan kayıt.
        var other = await CampaignAsync(w.Preparer, "diger", w.Segment.Id);
        var otherSnapshot = (await (await w.Preparer.PostAsync($"/api/marketing/campaigns/{other.Id}/audience/snapshot", null)).Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;
        var otherContent = await ContentAsync(w.Preparer, other.Id, "Konu", Body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: otherSnapshot.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, ContentId: otherContent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, ScheduledAt: DateTimeOffset.UtcNow.AddHours(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: Guid.NewGuid()))).StatusCode);

        // Bilinmeyen eylem ve olmayan çalıştırma.
        var run = await DraftRunAsync(w);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Preparer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/yoket", new RunActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{Guid.NewGuid()}/approve", new RunActionRequest())).StatusCode);

        // Taslak silinir; onaylanan silinemez.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Preparer.DeleteAsync($"/api/marketing/campaign-runs/{run.Id}?version={run.Version}")).StatusCode);
    }

    [Fact]
    public async Task A_scheduled_run_does_not_start_before_its_time_and_the_campaign_window_is_respected()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var future = await DraftRunAsync(w, scheduledAt: DateTimeOffset.UtcNow.AddHours(3));
        await ActAsync(w.Preparer, future.Id, "submit");
        await ActAsync(w.Approver, future.Id, "approve");
        var early = await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{future.Id}/start", new RunActionRequest());
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("zaman", await early.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await SummaryAsync(w.Approver, future.Id)).Total);

        // Kampanya dönemi bittiyse başlatılamaz.
        var campaign = (await w.Preparer.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{w.Campaign.Id}"))!;
        var response = await w.Preparer.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}",
            new SaveCampaignRequest(campaign.Version, campaign.Code, campaign.Name, campaign.Description, campaign.Purpose, campaign.Target, campaign.Channel, campaign.RecipientRule,
                campaign.RecipientRole, campaign.MaxPerTarget, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), campaign.OwnerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var now = await DraftRunAsync(w);
        await ActAsync(w.Preparer, now.Id, "submit");
        await ActAsync(w.Approver, now.Id, "approve");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{now.Id}/start", new RunActionRequest())).StatusCode);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Another_tenant_sees_and_does_nothing_with_runs_contents_and_deliveries()
    {
        var a = await WorldAsync();
        await SetSettingAsync(a.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(a);
        await ApproveAsync(a, run);
        await ActAsync(a.Approver, run.Id, "start");
        var b = await WorldAsync();

        Assert.Empty((await b.Preparer.GetFromJsonAsync<PagedResult<CampaignRunDto>>($"/api/marketing/campaign-runs?campaignId={run.CampaignId}"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Preparer.GetAsync($"/api/marketing/campaign-runs/{run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/cancel", new RunActionRequest(Reason: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Approver.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Approver.GetAsync($"/api/marketing/campaign-runs/{run.Id}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Approver.GetAsync($"/api/marketing/campaign-runs/{run.Id}/summary")).StatusCode);

        // B, A'nın anlık görüntüsüne ya da içeriğine kendi çalıştırmasını bağlayamaz.
        var snapshots = (await a.Preparer.GetFromJsonAsync<PagedResult<AudienceSnapshotDto>>("/api/marketing/audience-snapshots"))!.Items;
        var contents = (await a.Preparer.GetFromJsonAsync<PagedResult<CampaignContentDto>>("/api/marketing/campaign-contents"))!.Items;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await b.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: b.Campaign.Id, SnapshotId: snapshots[0].Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await b.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: b.Campaign.Id, ContentId: contents[0].Id))).StatusCode);

        // B'nin sağlayıcı ayarı A'nınkinden bağımsızdır: B'de sağlayıcı yok, gönderim bloklu.
        var runB = await PreparedRunAsync(b);
        await ApproveAsync(b, runB);
        Assert.Equal(HttpStatusCode.Conflict, (await b.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{runB.Id}/start", new RunActionRequest())).StatusCode);

        // Sıklık sınırı tenant başınadır.
        using var scopeA = factory.OpenScope(a.Tenant.Id);
        using var scopeB = factory.OpenScope(b.Tenant.Id);
        var day = new DateOnly(2026, 11, 3);
        Assert.True(await scopeA.ServiceProvider.GetRequiredService<IFrequencyLimiter>().TryReserveAsync(CommunicationChannel.Email, "ortak@x.test", day, 1, CancellationToken.None));
        Assert.False(await scopeA.ServiceProvider.GetRequiredService<IFrequencyLimiter>().TryReserveAsync(CommunicationChannel.Email, "ortak@x.test", day, 1, CancellationToken.None));
        Assert.True(await scopeB.ServiceProvider.GetRequiredService<IFrequencyLimiter>().TryReserveAsync(CommunicationChannel.Email, "ortak@x.test", day, 1, CancellationToken.None));
    }

    // ---- Yetki ----

    [Fact]
    public async Task Each_action_needs_its_own_permission_and_a_closed_module_stops_everything()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var viewer = await UserClientAsync(w.Tenant, [CampaignsRead]);
        var sender = await UserClientAsync(w.Tenant, [CampaignsRead, CampaignsSend, RecipientsRead]);
        var noRecipients = await UserClientAsync(w.Tenant, [CampaignsRead, CampaignsSend]);
        var run = await DraftRunAsync(w);

        // İzleyici ne hazırlayabilir ne onaylayabilir ne başlatabilir.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/submit", new RunActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/marketing/campaign-runs", new SaveCampaignRunRequest(CampaignId: w.Campaign.Id))).StatusCode);

        // Gönderim yetkisi onay yetkisi değildir; hazırlayan başlatamaz.
        await ActAsync(w.Preparer, run.Id, "submit");
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/approve", new RunActionRequest())).StatusCode);
        await ActAsync(w.Approver, run.Id, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Preparer.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest())).StatusCode);
        Assert.Equal("Running", (await ActAsync(sender, run.Id, "start")).Status);
        await ProcessAllAsync(sender, run.Id);

        // Teslimat satırları alıcı görüntüleme izni ister (sayılar istemez).
        Assert.Equal(HttpStatusCode.OK, (await noRecipients.GetAsync($"/api/marketing/campaign-runs/{run.Id}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRecipients.GetAsync($"/api/marketing/campaign-runs/{run.Id}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sender.GetAsync($"/api/marketing/campaign-runs/{run.Id}/deliveries")).StatusCode);

        using var anonymous = host.CreateClient(Browser());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process", null)).StatusCode);

        // Modül kapanınca işleme ve eylemler 403; veriler silinmez.
        var second = await PreparedRunAsync(w);
        await ApproveAsync(w, second);
        await ActAsync(w.Approver, second.Id, "start");
        await factory.SetModuleAsync(w.Tenant.Id, MarketingModule.ModuleKey, enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Approver.PostAsync($"/api/marketing/campaign-runs/{second.Id}/process", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Approver.PostAsJsonAsync($"/api/marketing/campaign-runs/{second.Id}/pause", new RunActionRequest())).StatusCode);
        await factory.SetModuleAsync(w.Tenant.Id, MarketingModule.ModuleKey, enabled: true);
        Assert.Equal("Running", (await w.Approver.GetFromJsonAsync<CampaignRunDto>($"/api/marketing/campaign-runs/{second.Id}"))!.Status);
        Assert.True((await SummaryAsync(w.Approver, second.Id)).Total > 0);
    }

    [Fact]
    public async Task Shared_access_keys_match_the_marketing_module_including_runs()
    {
        var modulePermissions = ((Modulix.Core.Contracts.Modules.IModule)new MarketingModule()).Permissions.Select(p => p.Key).Order().ToList();
        Assert.Equal(
            modulePermissions,
            typeof(MarketingAccess).GetFields().Where(f => f.Name != nameof(MarketingAccess.ModuleKey)).Select(f => (string)f.GetValue(null)!).Order());
        await Task.CompletedTask;
    }

    // ---- Abonelikten çıkma bağlantısı (dilim 6) ----

    [Fact]
    public async Task Every_message_carries_its_own_working_unsubscribe_link_and_the_next_run_honours_the_unsubscribe()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");
        await ProcessAllAsync(w.Approver, run.Id);

        // Sağlayıcıya giden her ileti, kişiye özel bir bağlantı ve tek tıkla çıkış adresi taşır; içerik metni olduğu gibi kalır, alt bilgi sona eklenir.
        var key = w.Tenant.Id.ToString("N");
        var mine = Sent.Where(r => r.Unsubscribe?.PageUrl.Contains(key, StringComparison.Ordinal) == true).ToList();
        Assert.True(mine.Count >= 5, mine.Count.ToString());
        Assert.All(mine, r =>
        {
            Assert.StartsWith(Body, r.Body, StringComparison.Ordinal);
            Assert.EndsWith(r.Unsubscribe!.PageUrl, r.Body, StringComparison.Ordinal);
            Assert.Contains("/api/public/communication/", r.Unsubscribe.OneClickUrl, StringComparison.Ordinal);
        });
        Assert.Equal(mine.Count, mine.Select(r => r.Unsubscribe!.PageUrl).Distinct().Count());

        // Alıcı bağlantıdaki sayfa uç noktasıyla çıkar.
        var message = mine.First(r => r.Address == "ok1@a.test");
        var token = message.Unsubscribe!.PageUrl[(message.Unsubscribe.PageUrl.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        using var anonymous = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync($"/api/public/communication/{key}/unsubscribe", new PublicCommunicationTokenRequest(token))).StatusCode);
        var preferences = (await w.Preparer.GetFromJsonAsync<PreferencePageDto>("/api/communication/preferences?address=ok1@a.test"))!;
        Assert.Equal(("Denied", "unsubscribe"), (Assert.Single(preferences.Items).Status, preferences.Items[0].Source));

        // Sonraki çalıştırma çıkışı gönderimden hemen önce görür: bu alıcıya ileti gitmez.
        var before = Sent.Count(r => r.Address == "ok1@a.test" && r.Unsubscribe!.PageUrl.Contains(key, StringComparison.Ordinal));
        var second = await PreparedRunAsync(w);
        await ApproveAsync(w, second);
        await ActAsync(w.Approver, second.Id, "start");
        await ProcessAllAsync(w.Approver, second.Id);
        Assert.Equal(1, (await SummaryAsync(w.Approver, second.Id)).Codes["Skipped:denied"]);
        Assert.Equal(before, Sent.Count(r => r.Address == "ok1@a.test" && r.Unsubscribe!.PageUrl.Contains(key, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_run_cannot_start_without_the_public_address_because_no_marketing_message_goes_out_without_an_unsubscribe_link()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);

        // Genel adresi (App:PublicUrl) olmayan bir ana bilgisayardan aynı tenant'a bağlanan onaylayıcı.
        var user = await factory.CreateUserAsync(w.Tenant);
        await factory.CreateRoleAsync(w.Tenant.Id, Approver, RecordScope.All, user);
        var noUrl = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await noUrl.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);

        var summary = await SummaryAsync(noUrl, run.Id);
        Assert.False(summary.UnsubscribeLinkAvailable);
        Assert.True((await SummaryAsync(w.Approver, run.Id)).UnsubscribeLinkAvailable);

        var blocked = await noUrl.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest());
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("App:PublicUrl", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, (await SummaryAsync(w.Approver, run.Id)).Total);

        // Adres ayarlı ana bilgisayardan başlatılabilir.
        Assert.Equal("Running", (await ActAsync(w.Approver, run.Id, "start")).Status);
    }

    // ---- Yardımcılar ----

    private sealed record World(
        Modulix.Core.Platform.Tenant Tenant, HttpClient Preparer, HttpClient Approver, CampaignDto Campaign, SegmentDto Segment, AudienceSnapshotDto Snapshot, CampaignContentDto Content);

    /// <summary>Beş firma, her birinin izinli birincil kişisi: ikisi sorunsuz, biri kalıcı, biri geçici hata, biri kabul sonrası kaybolan yanıt üretir.</summary>
    private async Task<World> WorldAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        await factory.SetModuleAsync(tenant.Id, MarketingModule.ModuleKey, enabled: true);
        var preparer = await UserClientAsync(tenant, Preparer);
        var approver = await UserClientAsync(tenant, Approver);

        foreach (var (name, email) in new[]
        {
            ("Anka", "ok1@a.test"), ("Bora", "ok2@b.test"), ("Can", "bad+fail-permanent@c.test"), ("Deniz", "retry+fail-transient@d.test"), ("Ece", "lost+timeout@e.test"),
        })
        {
            var company = await CompanyAsync(preparer, $"{name} Tekstil");
            var contact = await ContactAsync(preparer, company.Id, name, "Kişi", email);
            Assert.NotNull(contact);
            await ConsentAsync(preparer, email, "Granted");
        }

        var segment = await PublishedSegmentAsync(preparer);
        var campaign = await CampaignAsync(preparer, "antrepo", segment.Id);
        var snapshot = (await (await preparer.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;
        Assert.Equal(5, snapshot.EligibleCount);
        var content = await ContentAsync(preparer, campaign.Id, "Merhaba", Body);
        return new World(tenant, preparer, approver, campaign, segment, snapshot, content);
    }

    private static SaveCampaignRunRequest Save(CampaignRunDto r) => new(r.Version, r.CampaignId, r.SnapshotId, r.ContentId, r.ScheduledAt, r.OwnerId);

    private static async Task<CampaignRunDto> DraftRunAsync(World w, DateTimeOffset? scheduledAt = null)
    {
        var response = await w.Preparer.PostAsJsonAsync("/api/marketing/campaign-runs",
            new SaveCampaignRunRequest(CampaignId: w.Campaign.Id, SnapshotId: w.Snapshot.Id, ContentId: w.Content.Id, ScheduledAt: scheduledAt));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CampaignRunDto>())!;
    }

    /// <summary>Taslak oluşturur ve onaya gönderir (hazırlayan).</summary>
    private static async Task<CampaignRunDto> PreparedRunAsync(World w)
    {
        var run = await DraftRunAsync(w);
        return await ActAsync(w.Preparer, run.Id, "submit");
    }

    private static async Task ApproveAsync(World w, CampaignRunDto run) => await ActAsync(w.Approver, run.Id, "approve");

    private static async Task<CampaignRunDto> ActAsync(HttpClient client, Guid runId, string action, string? reason = null, HttpStatusCode expectStatus = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync($"/api/marketing/campaign-runs/{runId}/{action}", new RunActionRequest(Reason: reason));
        if (expectStatus != HttpStatusCode.OK)
        {
            Assert.Equal(expectStatus, response.StatusCode);
            return (await client.GetFromJsonAsync<CampaignRunDto>($"/api/marketing/campaign-runs/{runId}"))!;
        }

        Assert.True(response.IsSuccessStatusCode, $"{action}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CampaignRunDto>())!;
    }

    private static async Task<ProcessResultDto> ProcessAllAsync(HttpClient client, Guid runId)
    {
        ProcessResultDto? last = null;
        for (var i = 0; i < 10; i++)
        {
            var response = await client.PostAsync($"/api/marketing/campaign-runs/{runId}/process?batchSize=50", null);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                break;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = (await response.Content.ReadFromJsonAsync<ProcessResultDto>())!;
            if (last.Completed)
            {
                break;
            }
        }

        return last!;
    }

    private static async Task<RunSummaryDto> SummaryAsync(HttpClient client, Guid runId) =>
        (await client.GetFromJsonAsync<RunSummaryDto>($"/api/marketing/campaign-runs/{runId}/summary"))!;

    private static async Task<CampaignContentDto> ContentAsync(HttpClient client, Guid campaignId, string subject, string body)
    {
        var response = await client.PostAsJsonAsync("/api/marketing/campaign-contents", new SaveCampaignContentRequest(CampaignId: campaignId, Subject: subject, Body: body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CampaignContentDto>())!;
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(s => s.Key == key);
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, current.Version) }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<SegmentDto> PublishedSegmentAsync(HttpClient client)
    {
        // Firma adı koşulu: her firmanın adı "Tekstil" içerir.
        var condition = new System.Text.Json.Nodes.JsonObject { ["field"] = "crm.company.name", ["operator"] = "contains", ["values"] = new System.Text.Json.Nodes.JsonArray("Tekstil") };
        var created = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "hepsi", Name: "Hepsi", Target: "Company", Kind: "Dynamic", Draft: condition.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var segment = (await created.Content.ReadFromJsonAsync<SegmentDto>())!;
        var published = await client.PostAsJsonAsync($"/api/marketing/segments/{segment.Id}/publish", new SegmentActionRequest());
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (await published.Content.ReadFromJsonAsync<SegmentDto>())!;
    }

    private static async Task<CampaignDto> CampaignAsync(HttpClient client, string code, Guid segmentId)
    {
        var response = await client.PostAsJsonAsync("/api/marketing/campaigns", new SaveCampaignRequest(Code: code, Name: $"Kampanya {code}", Target: "Company", Channel: "Email", RecipientRule: "PrimaryContact"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var campaign = (await response.Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/marketing/campaign-audience", new SaveCampaignAudienceEntryRequest(CampaignId: campaign.Id, Mode: "Include", SegmentId: segmentId))).StatusCode);
        return campaign;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<ContactDto> ContactAsync(HttpClient client, Guid companyId, string first, string last, string email)
    {
        var response = await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var contact = (await response.Content.ReadFromJsonAsync<ContactDto>())!;
        Assert.True((await client.PostAsJsonAsync("/api/crm/company-contacts", new SaveCompanyContactRequest(companyId, contact.Id, IsPrimary: true))).IsSuccessStatusCode);
        Assert.True((await client.PostAsJsonAsync("/api/crm/person-contact-points", new SavePersonContactPointRequest(contact.Id, email, "Email"))).IsSuccessStatusCode);
        return contact;
    }

    private static async Task ConsentAsync(HttpClient client, string address, string status)
    {
        var request = new SaveConsentRequest("Email", address, "Marketing", status, "manual", "test-kaniti", null, null, null);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", request)).StatusCode);
    }

    private async Task<HttpClient> UserClientAsync(Modulix.Core.Platform.Tenant tenant, string[] permissions)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, RecordScope.All, user);
        return await LoginAsync(user);
    }

    private static WebApplicationFactoryClientOptions Browser() => new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false };

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = host.CreateClient(Browser());
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
