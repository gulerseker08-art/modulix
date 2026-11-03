using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Marketing;
using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Marketing;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 10: gönderim işçisi. İşçi yalnızca kullanıcının zaten başlattığı çalıştırmanın sıradaki satırlarını gönderir; başlatmaz, onaylamaz, duraklatılmış ya da
/// modülü kapalı tenant'a dokunmaz. Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki. Test sağlayıcısı iletiyi hiçbir yere göndermez.
/// </summary>
public partial class CampaignRunTests
{
    private MarketingSendRunner Worker => host.Services.GetRequiredService<MarketingSendRunner>();

    private async Task<(int Processed, bool Completed)> WorkUntilDoneAsync(Guid tenantId)
    {
        var processed = 0;
        for (var i = 0; i < 6; i++)
        {
            var (runs, _) = await Worker.RunTenantAsync(tenantId, CancellationToken.None);
            processed += runs.Sum(r => r.Processed);
            if (runs.Count > 0 && runs.All(r => r.Completed))
            {
                return (processed, true);
            }
        }

        return (processed, false);
    }

    [Fact]
    public async Task The_worker_sends_a_started_run_to_completion_with_the_same_honest_counts_as_manual_processing()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");

        // Hiç kullanıcı isteği yok: satırları işçi gönderir (geçici hata yeniden denenir, belirsiz sonuç kör yeniden denenmez).
        var (processed, completed) = await WorkUntilDoneAsync(w.Tenant.Id);
        Assert.True(completed);
        Assert.True(processed >= 5);

        var summary = await SummaryAsync(w.Approver, run.Id);
        Assert.Equal("Completed", summary.Status);
        Assert.Equal(2, summary.Statuses["Accepted"]);
        Assert.Equal(2, summary.Statuses["Failed"]);
        Assert.Equal(1, summary.Statuses["Unknown"]);
        Assert.Equal(1, summary.Codes["Unknown:timeout-after-accept"]);
        var unknown = (await w.Approver.GetFromJsonAsync<CampaignDeliveryPageDto>($"/api/marketing/campaign-runs/{run.Id}/deliveries?status=Unknown"))!;
        Assert.Equal(1, Assert.Single(unknown.Items).Attempts);

        // Tamamlanan çalıştırmada yeni iş yoktur: ikinci tur hiçbir şey göndermez.
        var (again, _) = await Worker.RunTenantAsync(w.Tenant.Id, CancellationToken.None);
        Assert.Empty(again);
    }

    [Fact]
    public async Task The_worker_does_not_start_approve_or_send_runs_that_are_not_running_and_reports_a_blocked_provider()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");

        // Onaylanmış ama başlatılmamış çalıştırma: işçi başlatmaz, satır açmaz.
        var approved = await PreparedRunAsync(w);
        await ApproveAsync(w, approved);
        var (idle, _) = await Worker.RunTenantAsync(w.Tenant.Id, CancellationToken.None);
        Assert.Empty(idle);
        Assert.Equal("Approved", (await ActAsync(w.Approver, approved.Id, "pause", expectStatus: HttpStatusCode.Conflict)).Status);
        Assert.Equal(0, (await SummaryAsync(w.Approver, approved.Id)).Total);

        // Duraklatılmış çalıştırma: sıradaki satırlar gönderilmez.
        await ActAsync(w.Approver, approved.Id, "start");
        await ActAsync(w.Approver, approved.Id, "pause");
        var (paused, _) = await Worker.RunTenantAsync(w.Tenant.Id, CancellationToken.None);
        Assert.Empty(paused);
        Assert.Equal(5, (await SummaryAsync(w.Approver, approved.Id)).Statuses["Queued"]);

        // Sürdürülünce sağlayıcı kapatılmışsa: gönderim bloklu, hata raporlanır, hiçbir satır gitmez.
        await ActAsync(w.Approver, approved.Id, "resume");
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "none");
        var (blocked, _) = await Worker.RunTenantAsync(w.Tenant.Id, CancellationToken.None);
        var report = Assert.Single(blocked);
        Assert.Equal(0, report.Processed);
        Assert.Contains("sağlayıcı", report.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, (await SummaryAsync(w.Approver, approved.Id)).Statuses["Queued"]);
    }

    [Fact]
    public async Task The_worker_only_touches_the_tenant_it_runs_for()
    {
        var a = await WorldAsync();
        var b = await WorldAsync();
        foreach (var w in new[] { a, b })
        {
            await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        }

        var runA = await PreparedRunAsync(a);
        await ApproveAsync(a, runA);
        await ActAsync(a.Approver, runA.Id, "start");
        var runB = await PreparedRunAsync(b);
        await ApproveAsync(b, runB);
        await ActAsync(b.Approver, runB.Id, "start");

        await WorkUntilDoneAsync(a.Tenant.Id);

        Assert.Equal("Completed", (await SummaryAsync(a.Approver, runA.Id)).Status);
        var untouched = await SummaryAsync(b.Approver, runB.Id);
        Assert.Equal("Running", untouched.Status);
        Assert.Equal(5, untouched.Statuses["Queued"]);
    }

    [Fact]
    public async Task The_worker_does_nothing_for_a_tenant_whose_marketing_module_is_off()
    {
        var w = await WorldAsync();
        await SetSettingAsync(w.Preparer, MarketingModule.SettingKeys.Provider, "fake");
        var run = await PreparedRunAsync(w);
        await ApproveAsync(w, run);
        await ActAsync(w.Approver, run.Id, "start");

        await factory.SetModuleAsync(w.Tenant.Id, MarketingModule.ModuleKey, enabled: false);
        var (runs, skipped) = await Worker.RunTenantAsync(w.Tenant.Id, CancellationToken.None);
        Assert.Empty(runs);
        Assert.False(skipped);

        await factory.SetModuleAsync(w.Tenant.Id, MarketingModule.ModuleKey, enabled: true);
        Assert.Equal(5, (await SummaryAsync(w.Approver, run.Id)).Statuses["Queued"]);
    }

    [Fact]
    public void The_worker_identity_cannot_approve_start_publish_or_touch_responses()
    {
        var subscription = Assert.Single(((IModule)new MarketingModule()).EventSubscriptions, s => s.Key == "marketing.sender");
        Assert.Equal(
            [MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsSend, MarketingModule.Permissions.CampaignsWrite, MarketingModule.Permissions.RecipientsRead],
            subscription.Permissions.Order().ToArray());
        Assert.DoesNotContain(MarketingModule.Permissions.CampaignsApprove, subscription.Permissions);
        Assert.DoesNotContain(MarketingModule.Permissions.SegmentsPublish, subscription.Permissions);
        Assert.DoesNotContain(MarketingModule.Permissions.ResponsesWrite, subscription.Permissions);
    }
}
