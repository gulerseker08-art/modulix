using System.Reflection;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Otomasyon ekranlarının (ADR-0027, Dilim 5a) etiketleri ve menü görünürlüğü. Menüyü gizlemek yetki değildir; sunucu uygular.</summary>
public class AutomationScreenTests
{
    private static AppNavigation Nav(params string[] permissions)
    {
        var session = new SessionState(new ApiClient(new HttpClient()));
        Set(session, nameof(SessionState.Access), new MyAccessResponse([], permissions));
        Set(session, nameof(SessionState.User), new MeResponse(Guid.NewGuid(), "a@example.com", "Test", Guid.NewGuid(), [], false));
        return new AppNavigation(session);
    }

    private static void Set(SessionState session, string property, object value) =>
        typeof(SessionState).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(session, value);

    private static AutomationRuleSummaryDto Rule(string status, bool hasDraft = false) =>
        new(Guid.NewGuid(), "Kural", "crm", "crm.lead", status, status == AutomationLabels.Draft ? null : 1, hasDraft, 100, DateTimeOffset.UtcNow, 1);

    [Fact]
    public void Automation_menu_entry_needs_the_read_permission_and_opens_the_administration_app()
    {
        var reader = Nav(CoreAccess.ReadAutomation);
        Assert.Contains(AppCatalog.Admin, reader.Roots().Select(r => r.Id));
        Assert.Contains(reader.MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation");

        // Yalnızca yönetme izni olan okuyamaz: menüde girdi çıkmaz (sunucu da 403 verir).
        var manageOnly = Nav(CoreAccess.ManageAutomation);
        Assert.DoesNotContain(manageOnly.MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation");

        Assert.DoesNotContain(Nav(CoreAccess.ReadAudit).MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation");

        // Yalnızca çalışma günlüğü izni olan kural listesini göremez ama geçmişe kendi girdisinden ulaşır (ADR-0027 kalan madde).
        var logsOnly = Nav(CoreAccess.ReadAutomationLogs);
        Assert.DoesNotContain(logsOnly.MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation");
        Assert.Contains(logsOnly.MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation-logs" && e.App.Route == "yonetim/otomasyon/calismalar");
        Assert.DoesNotContain(manageOnly.MenuEntries(AppCatalog.Admin), e => e.App.Id == "admin.automation-logs");
    }

    [Fact]
    public void Statuses_have_turkish_names_and_distinct_badge_tones()
    {
        Assert.Equal(["Taslak", "Açık", "Kapalı"], AutomationLabels.Statuses.Select(AutomationLabels.Status));
        Assert.Equal(3, AutomationLabels.Statuses.Select(AutomationLabels.StatusBadge).Distinct().Count());
        Assert.Equal("Bilinmeyen", AutomationLabels.Status("Bilinmeyen"));
    }

    [Fact]
    public void Behaviour_text_says_plainly_whether_the_rule_runs()
    {
        Assert.Contains("hiçbir şey yapmaz", AutomationLabels.Behaviour(Rule(AutomationLabels.Draft)));
        Assert.Equal("Çalışıyor.", AutomationLabels.Behaviour(Rule(AutomationLabels.Active)));
        Assert.Contains("eski sürüm geçerli", AutomationLabels.Behaviour(Rule(AutomationLabels.Active, hasDraft: true)));
        Assert.Contains("çalışmaz", AutomationLabels.Behaviour(Rule(AutomationLabels.Inactive)));
    }

    [Fact]
    public void Entity_name_comes_from_the_catalog_and_falls_back_to_the_technical_key()
    {
        AutomationEntityDto[] entities = [new("crm", "crm.lead", "Müşteri adayı", [], [], [])];

        Assert.Equal("Müşteri adayı", AutomationLabels.EntityName(entities, "crm.lead"));
        Assert.Equal("crm.opportunity", AutomationLabels.EntityName(entities, "crm.opportunity"));
    }

    // Dilim 5b: çalışma geçmişi etiketleri

    [Fact]
    public void Run_statuses_have_turkish_names_and_the_failed_and_succeeded_tones_differ()
    {
        Assert.Equal(
            ["Bekliyor", "Çalışıyor", "Başarılı", "Başarısız", "İptal edildi", "Koşul sağlanmadı"],
            AutomationLabels.RunStatuses.Select(AutomationLabels.RunStatus));
        Assert.NotEqual(AutomationLabels.RunStatusBadge(AutomationLabels.RunSucceeded), AutomationLabels.RunStatusBadge(AutomationLabels.RunFailed));
        Assert.Equal("Bilinmeyen", AutomationLabels.RunStatus("Bilinmeyen"));
    }

    [Fact]
    public void Retry_is_offered_only_for_failed_runs_and_cancel_only_for_unfinished_ones()
    {
        Assert.Equal([AutomationLabels.RunFailed], AutomationLabels.RunStatuses.Where(AutomationLabels.CanRetry));
        Assert.Equal(
            [AutomationLabels.RunPending, AutomationLabels.RunRunning], AutomationLabels.RunStatuses.Where(AutomationLabels.CanCancel));
    }

    [Fact]
    public void Origin_and_step_statuses_have_turkish_names()
    {
        Assert.Equal(["Olay", "Elle", "Günlük tarama"], new[] { "Event", "Manual", "Scan" }.Select(AutomationLabels.Origin));
        Assert.Equal("Atlandı", AutomationLabels.StepStatus("Skipped"));
        Assert.Equal("Tamamlandı", AutomationLabels.StepStatus("Succeeded"));
    }

    [Fact]
    public void Every_run_status_the_server_can_return_has_a_label_that_is_not_the_technical_name()
    {
        // Sunucudaki AutomationRunStatus enum'una yeni değer eklenirse etiketsiz kalıp ekranda İngilizce adıyla görünmesin.
        Assert.All(AutomationLabels.RunStatuses, s => Assert.NotEqual(s, AutomationLabels.RunStatus(s)));
        Assert.All(["Pending", "Succeeded", "Skipped", "Failed", "Cancelled"], s => Assert.NotEqual(s, AutomationLabels.StepStatus(s)));
    }
}
