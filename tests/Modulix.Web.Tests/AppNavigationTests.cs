using System.Reflection;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using Modulix.Shared.Orders;
using Modulix.Shared.Support;
using Modulix.Shared.Fleet;
using Modulix.Shared.Hr;
using Modulix.Shared.Logistics;
using Modulix.Shared.Warehouse;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// Sol menü ve uygulama paneli görünürlüğü: modül + izin kesişimi, boş grubun gizlenmesi, ev sahipliği (CRM menüsünde
/// Sözleşme/Sipariş/Pazarlama) ve route çözümü. Menüyü gizlemek yetkilendirme değildir; bunlar yalnızca arayüz görünürlüğü testidir.
/// </summary>
public class AppNavigationTests
{
    private static AppNavigation Nav(string[] modules, string[] permissions, bool platformAdmin = false)
    {
        var session = new SessionState(new ApiClient(new HttpClient()));
        Set(session, nameof(SessionState.Access), new MyAccessResponse(modules, permissions));
        Set(session, nameof(SessionState.User), new MeResponse(Guid.NewGuid(), "a@example.com", "Test", Guid.NewGuid(), [], platformAdmin));
        return new AppNavigation(session);
    }

    private static void Set(SessionState session, string property, object value) =>
        typeof(SessionState).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(session, value);

    private static string[] SalesUser =>
    [
        CrmAccess.CompaniesRead, CrmAccess.ContactsRead, CrmAccess.LeadsRead, CrmAccess.OpportunitiesRead, CrmAccess.QuotesRead,
        CrmAccess.ActivitiesRead, ContractsAccess.ContractsRead, OrdersAccess.OrdersRead, MarketingAccess.CampaignsRead,
    ];

    private static readonly string[] AllModules = [CrmAccess.ModuleKey, ContractsAccess.ModuleKey, OrdersAccess.ModuleKey, MarketingAccess.ModuleKey];

    private static IEnumerable<string> EntryNames(MenuContext menu, string groupId) =>
        menu.Sections.Where(s => s.Group?.Id == groupId).SelectMany(s => s.Entries).Select(e => e.Name);

    [Fact]
    public void Roots_list_only_enabled_apps_the_user_can_reach()
    {
        var nav = Nav([CrmAccess.ModuleKey], [CrmAccess.CompaniesRead]);

        Assert.Equal([AppCatalog.Crm], nav.Roots().Select(r => r.Id));
    }

    [Fact]
    public void User_without_any_access_sees_no_apps()
    {
        Assert.Empty(Nav([], []).Roots());
    }

    [Fact]
    public void Module_that_is_not_enabled_for_the_tenant_is_hidden_even_with_permission()
    {
        var nav = Nav([CrmAccess.ModuleKey], [CrmAccess.CompaniesRead, ContractsAccess.ContractsRead]);

        Assert.DoesNotContain(nav.Roots(), r => r.Id == AppCatalog.Contracts);
        Assert.DoesNotContain(nav.MenuEntries(AppCatalog.Crm), e => e.App.Id == "contracts.list");
    }

    [Fact]
    public void Contracts_orders_and_marketing_live_inside_crm_menu_when_crm_is_visible()
    {
        var nav = Nav(AllModules, SalesUser);

        Assert.Equal([AppCatalog.Crm], nav.Roots().Select(r => r.Id));

        var menu = nav.Context("crm/teklifler")!;
        Assert.Equal(AppCatalog.Crm, menu.Root.Id);
        Assert.Equal(["Müşteri adayları", "Fırsatlar", "Teklifler", "Satış sözleşmeleri", "Satış siparişleri"], EntryNames(menu, AppCatalog.CrmSalesGroup));
        Assert.Contains("Kampanyalar", EntryNames(menu, AppCatalog.CrmMarketingGroup));
    }

    [Fact]
    public void Crm_dashboard_is_first_in_the_workspace_for_users_with_sales_reports()
    {
        MenuContext menu = Nav(AllModules, [.. SalesUser, SalesAccess.ReportsRead]).Context("crm/pano")!;

        Assert.Equal(AppCatalog.Crm, menu.Root.Id);
        Assert.Equal("Pano", EntryNames(menu, AppCatalog.CrmWorkspaceGroup).First());
    }

    [Fact]
    public void Crm_dashboard_is_shown_to_a_rep_without_sales_reports_permission_for_my_day()
    {
        // Satış raporu izni olmayan temsilci panoyu yalnızca "Benim günüm" sekmesiyle görür (Hedef merkezi gizlidir).
        MenuContext menu = Nav(AllModules, SalesUser).Context("crm/firmalar")!;
        Assert.Contains("Pano", EntryNames(menu, AppCatalog.CrmWorkspaceGroup));

        // Fırsat, aday, teklif, aktivite ve rapor okuma izinlerinden hiçbiri yoksa pano yoktur.
        MenuContext none = Nav(AllModules, [CrmAccess.CompaniesRead]).Context("crm/firmalar")!;
        Assert.DoesNotContain("Pano", EntryNames(none, AppCatalog.CrmWorkspaceGroup));
    }

    [Fact]
    public void Hosted_screens_keep_their_own_routes_and_resolve_to_the_crm_menu()
    {
        var nav = Nav(AllModules, SalesUser);

        foreach (var (path, activeId) in new[] { ("sozlesmeler", "contracts.list"), ("sozlesmeler/123", "contracts.list"), ("siparisler", "orders.list"), ("pazarlama/kampanyalar", "marketing.campaigns") })
        {
            var menu = nav.Context(path)!;
            Assert.Equal(AppCatalog.Crm, menu.Root.Id);
            Assert.Equal(activeId, menu.Active.Id);
        }
    }

    [Fact]
    public void Contracts_stay_a_separate_app_when_the_tenant_has_no_crm()
    {
        var nav = Nav([ContractsAccess.ModuleKey], [ContractsAccess.ContractsRead]);

        Assert.Equal([AppCatalog.Contracts], nav.Roots().Select(r => r.Id));
        Assert.Equal(AppCatalog.Contracts, nav.Context("sozlesmeler")!.Root.Id);
    }

    [Fact]
    public void Hosted_screen_without_permission_is_not_listed_in_the_crm_menu()
    {
        var nav = Nav(AllModules, [CrmAccess.QuotesRead]);

        var names = nav.MenuEntries(AppCatalog.Crm).Select(e => e.Name).ToList();

        Assert.Contains("Teklifler", names);
        Assert.DoesNotContain("Satış sözleşmeleri", names);
        Assert.DoesNotContain("Satış siparişleri", names);
    }

    [Fact]
    public void Empty_sections_are_hidden()
    {
        var nav = Nav([CrmAccess.ModuleKey], [CrmAccess.CompaniesRead]);

        var menu = nav.Context("crm/firmalar")!;

        Assert.Equal([AppCatalog.CrmCustomersGroup], menu.Sections.Select(s => s.Group?.Id));
    }

    [Fact]
    public void Crm_settings_section_is_pinned_and_lists_only_what_the_user_can_open()
    {
        var plain = Nav(AllModules, SalesUser).Context("crm/teklifler")!;
        Assert.Equal(["Sözleşme şablonları"], EntryNames(plain, AppCatalog.CrmSettingsGroup));

        var noContracts = Nav([CrmAccess.ModuleKey], [CrmAccess.QuotesRead]).Context("crm/teklifler")!;
        Assert.DoesNotContain(noContracts.Sections, s => s.Group?.Id == AppCatalog.CrmSettingsGroup);

        var manager = Nav(AllModules, [.. SalesUser, CrmAccess.QuoteRulesManage]).Context("crm/teklifler")!;
        var settings = Assert.Single(manager.Sections, s => s.Group?.Id == AppCatalog.CrmSettingsGroup);
        Assert.True(settings.Group!.Footer);
        Assert.Contains(settings.Entries, e => e.Name == "Teklif kuralları");
    }

    [Fact]
    public void Module_settings_open_in_their_own_module_and_core_settings_stay_in_administration()
    {
        var nav = Nav(AllModules, [.. SalesUser, CoreAccess.ManageSettings]);

        var crm = nav.Context(AppCatalog.ModuleSettingsRoute(CrmAccess.ModuleKey))!;
        Assert.Equal((AppCatalog.Crm, "crm.settings"), (crm.Root.Id, crm.Active.Id));
        Assert.Contains("Genel CRM ayarları", EntryNames(crm, AppCatalog.CrmSettingsGroup));

        var contracts = nav.Context(AppCatalog.ModuleSettingsRoute(ContractsAccess.ModuleKey))!;
        Assert.Equal((AppCatalog.Crm, "contracts.settings"), (contracts.Root.Id, contracts.Active.Id));

        var core = nav.Context("yonetim/ayarlar")!;
        Assert.Equal((AppCatalog.Admin, "admin.settings"), (core.Root.Id, core.Active.Id));
        Assert.Equal("Kuruluş ayarları", core.Active.Name);
    }

    [Fact]
    public void Administration_links_only_the_settings_of_enabled_modules_for_users_who_can_change_them()
    {
        var crmOnly = Nav([CrmAccess.ModuleKey], [CoreAccess.ManageSettings]);
        Assert.Equal(["crm.settings"], crmOnly.ModuleSettings().Select(s => s.Id));

        var all = Nav([.. AllModules, SupportAccess.ModuleKey], [CoreAccess.ManageSettings]);
        Assert.Equal(5, all.ModuleSettings().Count);

        Assert.Empty(Nav(AllModules, SalesUser).ModuleSettings());
    }

    [Fact]
    public void Every_module_with_settings_has_a_settings_screen_keyed_by_its_module()
    {
        // Ekran ayarları modül anahtarıyla süzer: route'taki anahtar modülünkiyle aynı olmazsa ekran boş açılır.
        var screens = AppCatalog.All.Where(a => a.Route?.StartsWith("modul-ayarlari/", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(
            new[] { CrmAccess.ModuleKey, ContractsAccess.ModuleKey, OrdersAccess.ModuleKey, SupportAccess.ModuleKey, MarketingAccess.ModuleKey }.Order(),
            screens.Select(s => s.ModuleKey!).Order());
        Assert.All(screens, s => Assert.Equal(AppCatalog.ModuleSettingsRoute(s.ModuleKey!), s.Route));
    }

    [Fact]
    public void Platform_apps_are_visible_to_platform_administrators_only()
    {
        Assert.DoesNotContain(Nav([], []).Roots(), r => r.Id == AppCatalog.Platform);
        Assert.Contains(Nav([], [], platformAdmin: true).Roots(), r => r.Id == AppCatalog.Platform);
    }

    [Fact]
    public void Gtip_catalog_screen_is_in_the_platform_menu_for_platform_administrators_only()
    {
        Assert.Contains(Nav([], [], platformAdmin: true).Children(AppCatalog.Platform), c => c.Id == "platform.gtip" && c.Route == "platform/gtip");
        Assert.DoesNotContain(Nav(AllModules, SalesUser).Children(AppCatalog.Platform), c => c.Id == "platform.gtip");
    }

    [Fact]
    public void Regime_profiles_screen_is_in_the_platform_menu_for_platform_administrators_only()
    {
        Assert.Contains(Nav([], [], platformAdmin: true).Children(AppCatalog.Platform), c => c.Id == "platform.regime-profiles" && c.Route == "platform/rejim-profilleri");
        Assert.DoesNotContain(Nav(AllModules, SalesUser).Children(AppCatalog.Platform), c => c.Id == "platform.regime-profiles");
    }

    [Fact]
    public void Deep_link_to_a_screen_the_user_cannot_see_does_not_expose_that_menu()
    {
        var nav = Nav([CrmAccess.ModuleKey], [CrmAccess.CompaniesRead]);

        Assert.Null(nav.Context("destek/talepler"));
    }

    [Fact]
    public void Entry_route_of_an_app_without_own_route_is_its_first_menu_screen()
    {
        var nav = Nav(AllModules, SalesUser);

        Assert.Equal("crm/pano", nav.EntryRoute(nav.Find(AppCatalog.Crm)!));
    }

    [Fact]
    public void Detail_pages_keep_the_parent_list_active_and_do_not_activate_sibling_lists()
    {
        var nav = Nav(AllModules, SalesUser);

        Assert.Equal("crm.quotes", nav.Context("crm/teklifler/3fa85f64")!.Active.Id);
        Assert.Equal("contracts.templates", nav.Context("sozlesmeler/sablonlar")!.Active.Id);
    }

    [Fact]
    public void Quote_and_contract_approvals_share_one_pending_work_entry()
    {
        var nav = Nav(AllModules, [.. SalesUser, CrmAccess.QuotesApprove, ContractsAccess.ContractsApprove]);

        var menu = nav.Context("crm/teklifler")!;
        var workspace = EntryNames(menu, AppCatalog.CrmWorkspaceGroup).ToList();

        Assert.Equal(["Pano", "Aktiviteler", "Onay bekleyen işlerim"], workspace);

        // Karar ekranlarına doğrudan bağlantı ya da bildirim e-postası gelirse üst satır işaretlenir; sayfalar ayrı satır olmaz.
        foreach (var path in new[] { "crm/teklif-onaylari", "sozlesmeler/onaylar", "crm/onay-bekleyenler" })
        {
            var onApproval = nav.Context(path)!;
            Assert.Equal(AppCatalog.Crm, onApproval.Root.Id);
            Assert.Equal("crm.approvals", onApproval.Active.Id);
        }
    }

    [Fact]
    public void Pending_work_entry_needs_an_approval_permission_from_either_module()
    {
        Assert.DoesNotContain(Nav(AllModules, SalesUser).MenuEntries(AppCatalog.Crm), e => e.App.Id == "crm.approvals");
        Assert.Contains(Nav(AllModules, [CrmAccess.QuotesApprove]).MenuEntries(AppCatalog.Crm), e => e.App.Id == "crm.approvals");
        Assert.Contains(Nav(AllModules, [ContractsAccess.ContractsApprove]).MenuEntries(AppCatalog.Crm), e => e.App.Id == "crm.approvals");
    }

    [Fact]
    public void Contract_approvals_keep_their_own_menu_row_when_the_tenant_has_no_crm()
    {
        var nav = Nav([ContractsAccess.ModuleKey], [ContractsAccess.ContractsApprove]);

        var menu = nav.Context("sozlesmeler/onaylar")!;

        Assert.Equal(AppCatalog.Contracts, menu.Root.Id);
        Assert.Equal("contracts.approvals", menu.Active.Id);
    }

    [Fact]
    public void Sales_contracts_entry_opens_the_sales_view_of_the_shared_contracts_list()
    {
        var nav = Nav(AllModules, SalesUser);

        var entry = nav.MenuEntries(AppCatalog.Crm).Single(e => e.Name == "Satış sözleşmeleri");

        Assert.Equal("sozlesmeler?gorunum=sales", entry.Link);
        Assert.Equal("contracts.list", entry.App.Id);
        // Standalone (CRM'siz) uygulamada liste tüm sözleşmeleri açar: bağlantı kısayolun adresini taşımaz.
        Assert.Equal("sozlesmeler", nav.Find("contracts.list")!.Route);
    }

    [Fact]
    public void Warehouse_app_shows_the_facilities_screen_only_with_the_module_and_the_permission()
    {
        var nav = Nav([WarehouseAccess.ModuleKey], [WarehouseAccess.FacilitiesRead]);

        var root = Assert.Single(nav.Roots());
        Assert.Equal((AppCatalog.Warehouse, "Depo ve Antrepo"), (root.Id, root.Name));
        Assert.Equal("depo/tesisler", nav.Find("warehouse.facilities")!.Route);
        Assert.Equal(AppCatalog.Warehouse, nav.Context("depo/tesisler")!.Root.Id);

        // Modül kapalıysa ya da hiçbir depo izni yoksa uygulama görünmez (menüyü gizlemek yetki değildir; sunucu yine denetler).
        Assert.Empty(Nav([], [WarehouseAccess.FacilitiesRead]).Roots());
        Assert.Empty(Nav([WarehouseAccess.ModuleKey], []).Roots());
    }

    [Fact]
    public void Logistics_app_shows_the_drivers_screen_only_with_the_module_and_the_permission()
    {
        var nav = Nav([LogisticsAccess.ModuleKey], [LogisticsAccess.DriversRead]);

        var root = Assert.Single(nav.Roots());
        Assert.Equal((AppCatalog.Logistics, "Lojistik"), (root.Id, root.Name));
        Assert.Equal(["Pano", "Sürücüler", "Sürücü belgeleri"], nav.MenuEntries(AppCatalog.Logistics).Select(e => e.Name)); // belge takibi ve pano sürücü okuma iznine bağlı
        Assert.Equal(AppCatalog.Logistics, nav.Context("lojistik/suruculer")!.Root.Id);
        Assert.Equal(AppCatalog.Logistics, nav.Context("lojistik/surucu-belgeleri")!.Root.Id);

        Assert.Empty(Nav([], [LogisticsAccess.DriversRead]).Roots());
        Assert.Empty(Nav([LogisticsAccess.ModuleKey], []).Roots());

        // Taşıyıcı anlaşmaları kendi iznine bağlıdır: sürücü izni yetmez.
        Assert.Equal(["Pano", "Sürücüler", "Taşıyıcı anlaşmaları", "Sürücü belgeleri"], Nav([LogisticsAccess.ModuleKey], [LogisticsAccess.DriversRead, LogisticsAccess.CarriersRead]).MenuEntries(AppCatalog.Logistics).Select(e => e.Name));
        Assert.Equal(["Taşıyıcı anlaşmaları"], Nav([LogisticsAccess.ModuleKey], [LogisticsAccess.CarriersRead]).MenuEntries(AppCatalog.Logistics).Select(e => e.Name));

        // Pano talep, sevkiyat, sefer ya da sürücü okuma izinlerinden biriyle görünür; taşıyıcı izni yetmez.
        Assert.Equal("Pano", Nav([LogisticsAccess.ModuleKey], [LogisticsAccess.TripsRead]).MenuEntries(AppCatalog.Logistics).First().Name);
        Assert.Equal("lojistik/pano", Nav([LogisticsAccess.ModuleKey], [LogisticsAccess.TripsRead]).Find("logistics.dashboard")!.Route);
    }

    [Fact]
    public void Fleet_app_shows_the_vehicles_screen_only_with_the_module_and_the_permission()
    {
        var nav = Nav([FleetAccess.ModuleKey], [FleetAccess.VehiclesRead]);

        var root = Assert.Single(nav.Roots());
        Assert.Equal((AppCatalog.Fleet, "Filo"), (root.Id, root.Name));
        Assert.Equal("filo/araclar", nav.Find("fleet.vehicles")!.Route);
        Assert.Equal(AppCatalog.Fleet, nav.Context("filo/araclar")!.Root.Id);
        Assert.Equal(AppCatalog.Fleet, nav.Context("filo/belgeler")!.Root.Id);

        // Modül kapalıysa ya da araç izni yoksa uygulama görünmez (menüyü gizlemek yetki değildir; sunucu yine denetler).
        Assert.Empty(Nav([], [FleetAccess.VehiclesRead]).Roots());
        Assert.Empty(Nav([FleetAccess.ModuleKey], []).Roots());

        // Bakım kayıtları kendi iznine bağlıdır: araç izni yetmez.
        Assert.Equal(["Araçlar", "Araç belgeleri"], nav.MenuEntries(AppCatalog.Fleet).Select(e => e.Name)); // belge takibi araç okuma iznine bağlı
        Assert.Equal(["Araçlar", "Bakım kayıtları", "Araç belgeleri"], Nav([FleetAccess.ModuleKey], [FleetAccess.VehiclesRead, FleetAccess.MaintenanceRead]).MenuEntries(AppCatalog.Fleet).Select(e => e.Name));
    }

    [Fact]
    public void Each_warehouse_screen_follows_its_own_permission()
    {
        var all = Nav(
            [WarehouseAccess.ModuleKey],
            [WarehouseAccess.FacilitiesRead, WarehouseAccess.ZonesRead, WarehouseAccess.LocationLevelsRead, WarehouseAccess.LocationsRead, WarehouseAccess.AttorneysRead, WarehouseAccess.StockItemsRead]);
        Assert.Equal(
            new[] { "depo/tesisler", "depo/alanlar", "depo/lokasyon-seviyeleri", "depo/lokasyonlar" },
            new[] { "warehouse.facilities", "warehouse.zones", "warehouse.location-levels", "warehouse.locations" }.Select(id => all.Find(id)!.Route));

        // Yalnızca alan izni olan kişi yalnızca alanlar ekranını görür.
        var zonesOnly = Nav([WarehouseAccess.ModuleKey], [WarehouseAccess.ZonesRead]);
        Assert.Equal([AppCatalog.Warehouse], zonesOnly.Roots().Select(r => r.Id));
        Assert.Equal(["Alanlar"], zonesOnly.MenuEntries(AppCatalog.Warehouse).Select(e => e.Name));

        // Beyannameler ekranı yalnızca beyanname okuma iznine bağlıdır (K15: warehouse.customs.*); vekâlet ya da stok izni onu açmaz.
        var declarationsOnly = Nav([WarehouseAccess.ModuleKey], [WarehouseAccess.DeclarationsRead]);
        Assert.Equal(["Beyannameler"], declarationsOnly.MenuEntries(AppCatalog.Warehouse).Select(e => e.Name));
        Assert.Equal("depo/beyannameler", declarationsOnly.Find("warehouse.customs-declarations")!.Route);
        Assert.DoesNotContain("Beyannameler", Nav([WarehouseAccess.ModuleKey], [WarehouseAccess.AttorneysRead, WarehouseAccess.StockItemsRead])
            .MenuEntries(AppCatalog.Warehouse).Select(e => e.Name));

        // Rejim profilleri (bağlama) ekranı ayrı izne bağlıdır: beyanname izni onu açmaz, bağlama izni beyannameyi açmaz.
        var regimesOnly = Nav([WarehouseAccess.ModuleKey], [WarehouseAccess.RegimesRead]);
        Assert.Equal(["Rejim profilleri"], regimesOnly.MenuEntries(AppCatalog.Warehouse).Select(e => e.Name));
        Assert.Equal("depo/rejim-profilleri", regimesOnly.Find("warehouse.regime-bindings")!.Route);
        Assert.DoesNotContain("Rejim profilleri", declarationsOnly.MenuEntries(AppCatalog.Warehouse).Select(e => e.Name));
    }

    [Fact]
    public void Hr_app_shows_employees_directory_and_org_chart_only_with_the_module_and_the_permission()
    {
        var nav = Nav([HrAccess.ModuleKey], [HrAccess.EmployeesRead]);

        var root = Assert.Single(nav.Roots());
        Assert.Equal((AppCatalog.Hr, "İnsan Kaynakları"), (root.Id, root.Name));
        Assert.Equal(["Çalışanlar", "Rehber", "Organizasyon şeması"], nav.MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal("ik/calisanlar", nav.Find("hr.employees")!.Route);
        Assert.Equal(AppCatalog.Hr, nav.Context("ik/rehber")!.Root.Id);
        Assert.Equal(AppCatalog.Hr, nav.Context("ik/organizasyon-semasi")!.Root.Id);

        // İzin ekranları kendi izinlerine bağlıdır: çalışan "İzinlerim"i, yönetici talepleri, İK hakları ve tatilleri görür.
        Assert.Equal(["Çalışanlar", "Rehber", "Organizasyon şeması", "İzinlerim"],
            Nav([HrAccess.ModuleKey], [HrAccess.EmployeesRead, HrAccess.LeavesRead, HrAccess.LeavesRequest]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Contains("İzin talepleri", Nav([HrAccess.ModuleKey], [HrAccess.LeavesApprove]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["İzin talepleri", "İzin hakları", "Resmî tatiller"], Nav([HrAccess.ModuleKey], [HrAccess.LeavesManage]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));

        // Masraf: çalışan "Masraflarım"ı, onaylayan/İK/finans "Masraf beyanları"nı görür.
        Assert.Equal(["Masraflarım"], Nav([HrAccess.ModuleKey], [HrAccess.ExpensesRead, HrAccess.ExpensesRequest]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["Masraf beyanları"], Nav([HrAccess.ModuleKey], [HrAccess.ExpensesRead, HrAccess.ExpensesPay]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal("ik/masraf-beyanlari", Nav([HrAccess.ModuleKey], [HrAccess.ExpensesManage]).Find("hr.expense-claims")!.Route);

        // Zimmet: okuma izni "Zimmetlerim"i, yönetim izni "Demirbaşlar"ı açar.
        Assert.Equal(["Zimmetlerim"], Nav([HrAccess.ModuleKey], [HrAccess.AssetsRead]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["Zimmetlerim", "Demirbaşlar"], Nav([HrAccess.ModuleKey], [HrAccess.AssetsRead, HrAccess.AssetsManage]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));

        // İşe giriş/çıkış: okuma "Görevlerim"i, yönetim süreç ve şablon ekranlarını açar.
        Assert.Equal(["Görevlerim"], Nav([HrAccess.ModuleKey], [HrAccess.ChecklistsRead]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["Görevlerim", "İşe giriş ve çıkış", "Süreç şablonları"],
            Nav([HrAccess.ModuleKey], [HrAccess.ChecklistsRead, HrAccess.ChecklistsManage]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));

        // Duyurular: okuma akışı en üstte; yönetim ayrı ekran.
        Assert.Equal(["Duyurular"], Nav([HrAccess.ModuleKey], [HrAccess.AnnouncementsRead]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["Duyurular", "Duyuru yönetimi"],
            Nav([HrAccess.ModuleKey], [HrAccess.AnnouncementsRead, HrAccess.AnnouncementsManage]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));

        // Anketler: yanıtlama ekranı duyuruların ardından; yönetim ayrı ekran.
        Assert.Equal(["Duyurular", "Anketler"], Nav([HrAccess.ModuleKey], [HrAccess.AnnouncementsRead, HrAccess.SurveysRespond]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));
        Assert.Equal(["Anketler", "Anket yönetimi"],
            Nav([HrAccess.ModuleKey], [HrAccess.SurveysRespond, HrAccess.SurveysManage]).MenuEntries(AppCatalog.Hr).Select(e => e.Name));

        Assert.Empty(Nav([], [HrAccess.EmployeesRead]).Roots());
        Assert.Empty(Nav([HrAccess.ModuleKey], []).Roots());
    }

    [Fact]
    public void Planned_apps_are_listed_as_inert_cards_and_never_as_real_apps()
    {
        // Antrepo, Lojistik ve İnsan Kaynakları artık gerçek uygulamadır (AppCatalog.All); pasif kart olarak kalmaz.
        Assert.Empty(AppCatalog.Planned);

        // Pasif kart: route'u, menüsü ve yetkisi yoktur; gerçek bir uygulama kataloğa girince çakışmamalı.
        var realNames = AppCatalog.All.Where(a => a.ParentId is null).Select(a => a.Name).ToHashSet();
        Assert.All(AppCatalog.Planned, p => Assert.DoesNotContain(p.Name, realNames));
        Assert.DoesNotContain(Nav(AllModules, SalesUser).Roots(), r => AppCatalog.Planned.Any(p => p.Name == r.Name));
    }

    [Fact]
    public void Catalog_is_consistent()
    {
        var ids = AppCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var groupIds = AppCatalog.Groups.Select(g => g.Id).ToHashSet();
        Assert.Equal(groupIds.Count, AppCatalog.Groups.Count);

        foreach (var app in AppCatalog.All)
        {
            Assert.True(app.Group is null || groupIds.Contains(app.Group), $"{app.Id}: tanımsız grup {app.Group}");
            if (app.MenuParentId is { } parentId)
            {
                Assert.Contains(AppCatalog.All, a => a.Id == parentId && a.ParentId == app.ParentId);
            }

            if (app.Hosted is { } hosted)
            {
                Assert.True(groupIds.Contains(hosted.Group), $"{app.Id}: tanımsız ev sahibi grubu {hosted.Group}");
                Assert.True(hosted.ParentEntryId is null || AppCatalog.All.Any(a => a.Id == hosted.ParentEntryId), $"{app.Id}: tanımsız üst satır {hosted.ParentEntryId}");
                var root = AppCatalog.All.Single(a => a.Id == app.ParentId);
                Assert.NotNull(root.HostId);
            }

            if (app.HostId is not null)
            {
                Assert.Contains(AppCatalog.All, a => a.Id == app.HostId && a.ParentId is null);
            }
        }

        // Her ekranın route'u tektir: iki menü bağlantısı aynı sayfayı göstermez.
        var routes = AppCatalog.All.Where(a => a.Route is not null && !a.Route.Contains('?')).Select(a => a.Route!).ToList();
        Assert.Equal(routes.Count, routes.Distinct().Count());
    }
}
