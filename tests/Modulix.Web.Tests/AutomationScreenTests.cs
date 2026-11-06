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
}
