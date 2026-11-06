using Modulix.Shared.Access;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using Modulix.Shared.Orders;
using Modulix.Shared.Reporting;
using Modulix.Shared.Support;
using Modulix.Shared.Warehouse;

namespace Modulix.Web.Services;

public enum AppTone
{
    Indigo,
    Teal,
}

/// <summary>
/// Bir ekranın başka bir uygulamanın (ev sahibi) menüsünde görünüşü: hangi bölümde, hangi adla ve hangi sırada.
/// Yalnızca menü yerleşimidir; ekranın modülü, izni ve route'u değişmez.
/// </summary>
/// <param name="Group">Ev sahibi uygulamanın bölümü (<see cref="AppCatalog.Groups"/>).</param>
/// <param name="Name">Ev sahibinin menüsündeki ad; <c>null</c> ise ekranın kendi adı.</param>
/// <param name="Order">Bölüm içindeki sıra.</param>
/// <param name="Route">Ev sahibinin menüsündeki bağlantı (ör. hazır bir görünümle açan sorgu); <c>null</c> ise ekranın kendi route'u. Aktif ekran yine route yolundan bulunur.</param>
/// <param name="ParentEntryId">Verilirse ekran ev sahibinin menüsünde ayrı satır olmaz; bu menü satırının (kimlik) altında sayılır, o ekran aktifken o satır işaretlenir.</param>
public sealed record HostPlacement(string Group, string? Name, int Order, string? Route = null, string? ParentEntryId = null);

/// <summary>
/// Henüz ekranı olmayan, gelecekte eklenecek uygulama: panelde pasif ("Yakında") kart olarak görünür, tıklanınca bir şey yapmaz,
/// menüsü ve route'u yoktur. Gerçek uygulama katalogda tanımlanınca (<see cref="AppCatalog.All"/>) bu listeden çıkarılır.
/// </summary>
public sealed record PlannedApp(string Id, string Name, string Icon);

/// <summary>
/// Uygulama menüsünün (Modulix paneli) ve sol menünün ortak tanımı. <see cref="ParentId"/> boşsa ana uygulama (modül
/// ya da alan), doluysa onun alt uygulamasıdır. Görünürlük <see cref="AppNavigation"/> ile oturumdan hesaplanır;
/// menüde gizlemek yetkilendirme değildir, route ve sunucu denetimleri aynen geçerlidir.
/// </summary>
/// <param name="Id">Benzersiz uygulama kimliği.</param>
/// <param name="ParentId">Üst uygulamanın kimliği; ana uygulamada <c>null</c>.</param>
/// <param name="Name">Kullanıcıya görünen ad.</param>
/// <param name="Icon">İkon adı (<c>AppIcon</c>).</param>
/// <param name="Route">Tabana göreli route; ana uygulamada yalnızca kendi giriş ekranı varsa verilir.</param>
/// <param name="Order">Aynı düzeydeki sıralama.</param>
/// <param name="ModuleKey">Tenant için açık olması gereken modül; alan (Yönetim, Platform) için <c>null</c>.</param>
/// <param name="Permission">Gereken izin; <c>null</c> ise <paramref name="AnyPermission"/> ya da yalnızca modül denetlenir.</param>
/// <param name="AnyPermission">Bunlardan en az biri gerekir.</param>
/// <param name="PlatformOnly">Yalnızca platform yöneticisi görür.</param>
/// <param name="ExactMatch">Aktif sayfa yalnızca route tam eşleşince bu uygulamadır (genel bakış ekranları).</param>
/// <param name="Tone">Panel ikonunun rengi.</param>
/// <param name="Group">Uygulamanın panelde altında listelendiği başlık (<see cref="AppCatalog.Groups"/>): ana uygulamada alan başlığı, alt uygulamada kendi uygulamasının içindeki bölüm; <c>null</c> ise gruplanmaz.</param>
/// <param name="HostId">Ana uygulamada: bu uygulamanın ekranlarını kendi menüsünde gösteren uygulama (ör. CRM). Ev sahibi görünürse bu uygulama panelde ayrı kart olmaz; görünmüyorsa (ör. tenant'ta CRM yok) eskisi gibi ayrı uygulama olarak kalır.</param>
/// <param name="Hosted">Alt uygulamada: ev sahibinin menüsündeki yeri; <c>null</c> ise ev sahibinin menüsünde görünmez.</param>
/// <param name="MenuParentId">Alt uygulamada: bu ekran menüde ayrı satır olmaz, belirtilen ekranın (aynı uygulamada) altında sayılır; route'u çalışmaya devam eder.</param>
public sealed record AppDefinition(
    string Id,
    string? ParentId,
    string Name,
    string Icon,
    string? Route,
    int Order,
    string? ModuleKey = null,
    string? Permission = null,
    IReadOnlyList<string>? AnyPermission = null,
    bool PlatformOnly = false,
    bool ExactMatch = false,
    AppTone Tone = AppTone.Indigo,
    string? Group = null,
    string? HostId = null,
    HostPlacement? Hosted = null,
    string? MenuParentId = null);

/// <summary>
/// Panelde ana uygulamaları ya da sol menüde bir uygulamanın ekranlarını bir araya getiren başlık. Yalnızca görsel
/// kümelemedir: modül sahipliğini, izinleri ve route'ları etkilemez; hiçbir ekranı görünmeyen grup çıkmaz.
/// </summary>
/// <param name="Id">Benzersiz grup kimliği.</param>
/// <param name="Name">Kullanıcıya görünen başlık.</param>
/// <param name="Order">Gruplar arası sıralama.</param>
/// <param name="Icon">Sol menüde bölüm başlığının ikonu.</param>
/// <param name="Footer">Sol menüde kaydırma alanının altına sabitlenen bölüm (ör. CRM Ayarları).</param>
public sealed record AppGroup(string Id, string Name, int Order, string? Icon = null, bool Footer = false);

/// <summary>
/// Projedeki gerçek modül ve ekranların tanımı. Yeni modül ya da ekran eklemek bu listeye satır eklemektir; panel ve
/// sol menü başka yerde sabit yazılmaz. Ekranı olmayan uygulama (ör. Lojistik; CRM'in Takvim, Kanal ve İş Ortakları,
/// Müşteri Başlangıç Planları ekranları) buraya eklenmez: boş menü bağlantısı yoktur.
/// </summary>
public static class AppCatalog
{
    public const string Crm = "crm";
    public const string Contracts = "contracts";
    public const string Orders = "orders";
    public const string Support = "support";
    public const string Marketing = "marketing";
    public const string Warehouse = "warehouse";
    public const string Reports = "reports";
    public const string Admin = "admin";
    public const string Platform = "platform";

    public const string SalesGroup = "sales";
    public const string ServiceGroup = "service";
    public const string OperationsGroup = "operations";
    public const string ManagementGroup = "management";

    public const string CrmWorkspaceGroup = "crm.workspace";
    public const string CrmCustomersGroup = "crm.customers";
    public const string CrmSalesGroup = "crm.sales";
    public const string CrmCatalogGroup = "crm.catalog";
    public const string CrmMarketingGroup = "crm.marketing";
    public const string CrmAfterSalesGroup = "crm.aftersales";
    public const string CrmReportsGroup = "crm.reports";
    public const string CrmSettingsGroup = "crm.settings";

    /// <summary>Hem ana uygulamaların hem de bir uygulamanın sol menü bölümlerinin başlıkları; bir tanım <c>Group</c> ile birini seçer.</summary>
    public static IReadOnlyList<AppGroup> Groups { get; } =
    [
        new(SalesGroup, "Satış ve müşteri", 10),
        new(ServiceGroup, "Müşteri hizmetleri", 20),
        new(OperationsGroup, "Operasyon", 25),
        new(ManagementGroup, "Yönetim", 30),

        new(CrmWorkspaceGroup, "Çalışma Alanım", 10, "home"),
        new(CrmCustomersGroup, "Müşteriler", 20, "users"),
        new(CrmSalesGroup, "Satış", 30, "trending-up"),
        new(CrmCatalogGroup, "Ürün ve Fiyatlandırma", 40, "tag"),
        new(CrmMarketingGroup, "Pazarlama", 50, "globe"),
        new(CrmAfterSalesGroup, "Satış Sonrası", 60, "repeat"),
        new(CrmReportsGroup, "Satış Raporları", 70, "dashboard"),
        new(CrmSettingsGroup, "CRM Ayarları", 100, "wrench", Footer: true),
    ];

    /// <summary>
    /// Yol haritasındaki uygulamalar (panelde pasif kart). Ekranı olan bir uygulama <see cref="All"/>'a eklendiğinde buradan silinir.
    /// </summary>
    public static IReadOnlyList<PlannedApp> Planned { get; } =
    [
        new("planned.logistics", "Lojistik", "truck"),
        new("planned.hr", "İnsan Kaynakları", "users"),
    ];

    public static IReadOnlyList<AppDefinition> All { get; } =
    [
        new(Crm, null, "CRM", "users", null, 10, ModuleKey: CrmAccess.ModuleKey, Group: SalesGroup),

        // Çalışma Alanım: kullanıcının üzerinde işlem bekleyen işler. "Onay bekleyen işlerim" teklif ve sözleşme onaylarını tek listede toplar;
        // karar ekranları (teklif onayları, sözleşme onayları) menüde ayrı satır değildir, bu satırın altında sayılır.
        new("crm.activities", Crm, "Aktiviteler", "activity", "crm/aktiviteler", 10, CrmAccess.ModuleKey, CrmAccess.ActivitiesRead, Tone: AppTone.Teal, Group: CrmWorkspaceGroup),
        new("crm.approvals", Crm, "Onay bekleyen işlerim", "check-circle", "crm/onay-bekleyenler", 20, CrmAccess.ModuleKey, AnyPermission: [CrmAccess.QuotesApprove, ContractsAccess.ContractsApprove], Tone: AppTone.Teal, Group: CrmWorkspaceGroup),
        new("crm.quote-approvals", Crm, "Teklif onayları", "check-circle", "crm/teklif-onaylari", 21, CrmAccess.ModuleKey, CrmAccess.QuotesApprove, Tone: AppTone.Teal, Group: CrmWorkspaceGroup, MenuParentId: "crm.approvals"),

        // Müşteriler
        new("crm.companies", Crm, "Firmalar", "building", "crm/firmalar", 10, CrmAccess.ModuleKey, CrmAccess.CompaniesRead, Group: CrmCustomersGroup),
        new("crm.contacts", Crm, "Kişiler", "user", "crm/kisiler", 20, CrmAccess.ModuleKey, CrmAccess.ContactsRead, Tone: AppTone.Teal, Group: CrmCustomersGroup),

        // Satış
        new("crm.leads", Crm, "Müşteri adayları", "inbox", "crm/leadler", 10, CrmAccess.ModuleKey, CrmAccess.LeadsRead, Group: CrmSalesGroup),
        new("crm.opportunities", Crm, "Fırsatlar", "target", "crm/firsatlar", 20, CrmAccess.ModuleKey, CrmAccess.OpportunitiesRead, Group: CrmSalesGroup),
        new("crm.quotes", Crm, "Teklifler", "file-text", "crm/teklifler", 30, CrmAccess.ModuleKey, CrmAccess.QuotesRead, Group: CrmSalesGroup),

        // Ürün ve Fiyatlandırma
        new("crm.products", Crm, "Ürün ve hizmetler", "package", "crm/urunler", 10, CrmAccess.ModuleKey, CrmAccess.ProductsRead, Group: CrmCatalogGroup),
        new("crm.price-lists", Crm, "Fiyat listeleri", "list", "crm/fiyat-listeleri", 20, CrmAccess.ModuleKey, CrmAccess.PricesRead, Tone: AppTone.Teal, Group: CrmCatalogGroup),
        new("crm.discounts", Crm, "İndirim kuralları", "zap", "crm/indirimler", 30, CrmAccess.ModuleKey, CrmAccess.PricesRead, Tone: AppTone.Teal, Group: CrmCatalogGroup),
        new("crm.price-calculator", Crm, "Fiyat hesaplama", "search", "crm/fiyat-hesapla", 40, CrmAccess.ModuleKey, CrmAccess.PricesRead, Group: CrmCatalogGroup),

        // Satış Raporları
        new("crm.sales-overview", Crm, "Satış aşamaları görünümü", "dashboard", "crm/satis-gorunumu", 10, CrmAccess.ModuleKey, SalesAccess.ReportsRead, Group: CrmReportsGroup),
        new("crm.sales-targets", Crm, "Satış hedefleri", "target", "crm/satis-hedefleri", 20, CrmAccess.ModuleKey, SalesAccess.TargetsRead, Tone: AppTone.Teal, Group: CrmReportsGroup),
        new("crm.sales-forecasts", Crm, "Satış tahmini", "trending-up", "crm/satis-tahmini", 30, CrmAccess.ModuleKey, SalesAccess.ForecastsRead, Group: CrmReportsGroup),
        new("crm.sales-analysis", Crm, "Satış analizi", "percent", "crm/satis-analizi", 40, CrmAccess.ModuleKey, SalesAccess.ReportsRead, Tone: AppTone.Teal, Group: CrmReportsGroup),
        new("crm.sales-team", Crm, "Ekip performansı", "users", "crm/ekip-performansi", 50, CrmAccess.ModuleKey, SalesAccess.ReportsRead, Group: CrmReportsGroup),

        // CRM Ayarları: CRM'e özgü kurallar ve tanımlar, veri kalitesi.
        new("crm.quote-rules", Crm, "Teklif kuralları", "shield", "crm/teklif-kurallari", 10, CrmAccess.ModuleKey, CrmAccess.QuoteRulesManage, Group: CrmSettingsGroup),
        new("crm.quote-delegations", Crm, "Onay vekâletleri", "users", "crm/onay-vekaletleri", 20, CrmAccess.ModuleKey, CrmAccess.QuoteRulesManage, Group: CrmSettingsGroup),
        new("crm.sales-data-quality", Crm, "Veri kalitesi", "shield", "crm/veri-kalitesi", 30, CrmAccess.ModuleKey, SalesAccess.ReportsRead, Tone: AppTone.Teal, Group: CrmSettingsGroup),
        new("crm.product-categories", Crm, "Ürün kategorileri", "layers", "crm/urun-kategorileri", 40, CrmAccess.ModuleKey, CrmAccess.ProductsRead, Group: CrmSettingsGroup),
        new("crm.units", Crm, "Ölçü birimleri", "sliders", "crm/birimler", 50, CrmAccess.ModuleKey, CrmAccess.ProductsRead, Group: CrmSettingsGroup),
        new("crm.tax-rates", Crm, "Vergi ve kur", "landmark", "crm/vergi-kur", 60, CrmAccess.ModuleKey, CrmAccess.PricesRead, Group: CrmSettingsGroup),

        // Sözleşmeler, Siparişler ve Pazarlama kendi modülleri, izinleri ve route'larıyla ayrı uygulamalar olarak kalır;
        // CRM görünürse ekranları CRM'in menüsünde (Hosted) gösterilir, görünmüyorsa eskisi gibi ayrı uygulamadır.
        new(Contracts, null, "Sözleşmeler", "file-text", null, 20, ModuleKey: ContractsAccess.ModuleKey, Tone: AppTone.Teal, Group: SalesGroup, HostId: Crm),
        new("contracts.dashboard", Contracts, "Pano", "dashboard", "sozlesmeler/ozet", 5, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, ExactMatch: true, Hosted: new(CrmReportsGroup, "Sözleşme panosu", 60)),
        new("contracts.list", Contracts, "Sözleşmeler", "file-text", "sozlesmeler", 10, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Hosted: new(CrmSalesGroup, "Satış sözleşmeleri", 40, Route: "sozlesmeler?gorunum=sales")),
        new("contracts.templates", Contracts, "Şablonlar", "layers", "sozlesmeler/sablonlar", 20, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Hosted: new(CrmSettingsGroup, "Sözleşme şablonları", 70)),
        new("contracts.approvals", Contracts, "Onaylar", "check-circle", "sozlesmeler/onaylar", 30, ContractsAccess.ModuleKey, ContractsAccess.ContractsApprove, Tone: AppTone.Teal, Hosted: new(CrmWorkspaceGroup, "Sözleşme onayları", 30, ParentEntryId: "crm.approvals")),
        new("contracts.signatures", Contracts, "İmzalar", "check-circle", "sozlesmeler/imzalar", 35, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Hosted: new(CrmAfterSalesGroup, "Sözleşme imzaları", 30)),
        new("contracts.commitments", Contracts, "Taahhütler", "clipboard-list", "sozlesmeler/taahhutler", 36, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Tone: AppTone.Teal, Hosted: new(CrmAfterSalesGroup, null, 20)),
        new("contracts.renewals", Contracts, "Yenilemeler", "calendar", "sozlesmeler/yenileme", 37, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Hosted: new(CrmAfterSalesGroup, "Yenileme takibi", 10)),
        new("contracts.settings", Contracts, "Sözleşme ayarları", "wrench", "yonetim/ayarlar?grup=S%C3%B6zle%C5%9Fmeler", 90, ContractsAccess.ModuleKey, CoreAccess.ManageSettings, Hosted: new(CrmSettingsGroup, null, 90)),
        new("contracts.approval-rules", Contracts, "Onay kuralları", "shield", "sozlesmeler/onay-kurallari", 40, ContractsAccess.ModuleKey, ContractsAccess.ApprovalsManage, Hosted: new(CrmSettingsGroup, "Sözleşme onay kuralları", 80)),

        new(Orders, null, "Siparişler", "package", null, 25, ModuleKey: OrdersAccess.ModuleKey, Group: SalesGroup, HostId: Crm),
        new("orders.dashboard", Orders, "Pano", "dashboard", "siparisler/ozet", 5, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead, ExactMatch: true, Hosted: new(CrmReportsGroup, "Sipariş panosu", 70)),
        new("orders.list", Orders, "Siparişler", "package", "siparisler", 10, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead, Hosted: new(CrmSalesGroup, "Satış siparişleri", 50)),
        new("orders.jobs", Orders, "Operasyon işleri", "clipboard-list", "siparisler/isler", 20, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead, Tone: AppTone.Teal, Hosted: new(CrmAfterSalesGroup, null, 40)),

        new(Support, null, "Destek", "message-square", null, 27, ModuleKey: SupportAccess.ModuleKey, Tone: AppTone.Teal, Group: ServiceGroup),
        new("support.tickets", Support, "Talepler", "inbox", "destek/talepler", 10, SupportAccess.ModuleKey, SupportAccess.TicketsRead, ExactMatch: true),
        new("support.new", Support, "Yeni talep", "clipboard-list", "destek/talepler/yeni", 20, SupportAccess.ModuleKey, SupportAccess.TicketsCreate, ExactMatch: true),
        new("support.settings", Support, "Kuyruklar ve kategoriler", "wrench", "destek/ayarlar", 90, SupportAccess.ModuleKey, SupportAccess.SettingsManage),
        new("support.routing", Support, "Öncelik ve yönlendirme", "list", "destek/yonlendirme", 89, SupportAccess.ModuleKey, SupportAccess.SettingsManage),
        new("support.sla", Support, "SLA ve eskalasyon", "shield", "destek/sla", 91, SupportAccess.ModuleKey, SupportAccess.SlaManage),
        new("support.reports", Support, "Raporlar", "list", "destek/raporlar", 92, SupportAccess.ModuleKey, SupportAccess.ReportsRead),
        new("support.complaints", Support, "Şikâyet inceleme", "shield", "destek/sikayetler", 93, SupportAccess.ModuleKey, SupportAccess.ReportsRead),

        new(Marketing, null, "Pazarlama", "trending-up", null, 28, ModuleKey: MarketingAccess.ModuleKey, Group: SalesGroup, HostId: Crm),
        new("marketing.segments", Marketing, "Segmentler", "list", "pazarlama/segmentler", 10, MarketingAccess.ModuleKey, MarketingAccess.SegmentsRead, Hosted: new(CrmMarketingGroup, null, 10)),
        new("marketing.campaigns", Marketing, "Kampanyalar", "target", "pazarlama/kampanyalar", 20, MarketingAccess.ModuleKey, MarketingAccess.CampaignsRead, Tone: AppTone.Teal, Hosted: new(CrmMarketingGroup, null, 20)),
        new("marketing.report", Marketing, "Kampanya raporu", "dashboard", "pazarlama/rapor", 30, MarketingAccess.ModuleKey, MarketingAccess.ReportsRead, Hosted: new(CrmMarketingGroup, "Kampanya sonuçları", 30)),

        // Depo ve antrepo (ADR-0032): şimdilik yapı tanımı (tesis, alan, lokasyon seviyesi, lokasyon); stok ve gümrük ekranları sonraki dilimlerle gelir.
        new(Warehouse, null, "Depo ve Antrepo", "warehouse", null, 40, ModuleKey: WarehouseAccess.ModuleKey, Group: OperationsGroup),
        new("warehouse.facilities", Warehouse, "Tesisler", "building", "depo/tesisler", 10, WarehouseAccess.ModuleKey, WarehouseAccess.FacilitiesRead),
        new("warehouse.zones", Warehouse, "Alanlar", "layers", "depo/alanlar", 20, WarehouseAccess.ModuleKey, WarehouseAccess.ZonesRead, Tone: AppTone.Teal),
        new("warehouse.location-levels", Warehouse, "Lokasyon seviyeleri", "sliders", "depo/lokasyon-seviyeleri", 30, WarehouseAccess.ModuleKey, WarehouseAccess.LocationLevelsRead),
        new("warehouse.locations", Warehouse, "Lokasyonlar", "map-pin", "depo/lokasyonlar", 40, WarehouseAccess.ModuleKey, WarehouseAccess.LocationsRead, Tone: AppTone.Teal),
        new("warehouse.power-of-attorneys", Warehouse, "Vekâletler", "file-text", "depo/vekaletler", 50, WarehouseAccess.ModuleKey, WarehouseAccess.AttorneysRead),
        new("warehouse.stock-items", Warehouse, "Stok kartları", "package", "depo/stok-kartlari", 60, WarehouseAccess.ModuleKey, WarehouseAccess.StockItemsRead, Tone: AppTone.Teal),
        new("warehouse.customs-declarations", Warehouse, "Beyannameler", "clipboard-list", "depo/beyannameler", 70, WarehouseAccess.ModuleKey, WarehouseAccess.DeclarationsRead),
        new("warehouse.regime-bindings", Warehouse, "Rejim profilleri", "shield", "depo/rejim-profilleri", 80, WarehouseAccess.ModuleKey, WarehouseAccess.RegimesRead, Tone: AppTone.Teal),

        // Rapor merkezi çekirdeğe aittir (modül anahtarı yok; yönetim girdileri gibi yalnızca izne bağlıdır). Tek ekranlı olduğu için alt uygulaması yoktur,
        // kendi route'u vardır; /raporlar altındaki oluşturucu ve çalıştırma ekranları da bu uygulamanın bağlamında kalır.
        new(Reports, null, "Raporlar", "file-text", "raporlar", 85, Permission: ReportAccess.Read, Tone: AppTone.Teal),

        new(Admin, null, "Yönetim", "sliders", null, 90, AnyPermission: CoreAccess.AdministrationPermissions, Tone: AppTone.Teal, Group: ManagementGroup),
        new("admin.home", Admin, "Genel bakış", "dashboard", "yonetim", 10, AnyPermission: CoreAccess.AdministrationPermissions, ExactMatch: true),
        new("admin.users", Admin, "Kullanıcılar", "user-check", "yonetim/kullanicilar", 20, Permission: CoreAccess.ManageUsers, Tone: AppTone.Teal),
        new("admin.teams", Admin, "Ekipler", "users", "yonetim/ekipler", 30, Permission: CoreAccess.ManageUsers),
        new("admin.business-units", Admin, "Departmanlar", "building", "yonetim/departmanlar", 35, Permission: CoreAccess.ManageUsers),
        new("admin.roles", Admin, "Roller", "key", "yonetim/roller", 40, Permission: CoreAccess.ManageRoles, Tone: AppTone.Teal),
        new("admin.companies", Admin, "Şirketler", "landmark", "yonetim/sirketler", 50, Permission: CoreAccess.ManageCompanies),
        new("admin.settings", Admin, "Firma ayarları", "wrench", "yonetim/ayarlar", 60, Permission: CoreAccess.ManageSettings, Tone: AppTone.Teal),
        new("admin.audit", Admin, "Denetim kaydı", "history", "yonetim/denetim", 70, Permission: CoreAccess.ReadAudit),
        new("admin.import-log", Admin, "İşlem günlüğü", "history", "yonetim/gunluk", 72, AnyPermission: [CoreAccess.ReadImports, CoreAccess.RunImports]),
        new("admin.communication", Admin, "İletişim tercihleri", "mail", "yonetim/iletisim-tercihleri", 75, Permission: CoreAccess.ReadCommunication),
        new("admin.custom-fields", Admin, "Özel alanlar", "square-pen", "yonetim/ozel-alanlar", 80, Permission: CoreAccess.ManageCustomization, Tone: AppTone.Teal),
        new("admin.lists", Admin, "Listeler", "list", "yonetim/listeler", 90, Permission: CoreAccess.ManageCustomization),
        new("admin.sector-profile", Admin, "Sektör profili", "layers", "yonetim/sektor-profili", 100, Permission: CoreAccess.ManageCustomization, Tone: AppTone.Teal),
        new("admin.activities", Admin, "Faaliyetlerim", "layers", "yonetim/faaliyetler", 105, Permission: CoreAccess.ManageCustomization),
        new("admin.form-designer", Admin, "Form tasarımı", "clipboard-list", "yonetim/form-tasarimi", 85, Permission: CoreAccess.ManageCustomization),

        new(Platform, null, "Platform", "shield", null, 100, PlatformOnly: true, Group: ManagementGroup),
        new("platform.home", Platform, "Genel bakış", "dashboard", "platform", 10, PlatformOnly: true, ExactMatch: true),
        new("platform.tenants", Platform, "Firmalar", "building", "platform/firmalar", 20, PlatformOnly: true, Tone: AppTone.Teal),
        new("platform.modules", Platform, "Modül hakları", "toggle", "platform/moduller", 30, PlatformOnly: true),
        new("platform.catalog", Platform, "Modül kataloğu", "package", "platform/katalog", 40, PlatformOnly: true, Tone: AppTone.Teal),
        new("platform.sectors", Platform, "Sektörler", "layers", "platform/sektorler", 45, PlatformOnly: true),
        new("platform.gtip", Platform, "GTİP kataloğu", "clipboard-list", "platform/gtip", 47, PlatformOnly: true),
        new("platform.regime-profiles", Platform, "Rejim profilleri", "shield", "platform/rejim-profilleri", 48, PlatformOnly: true),
        new("platform.events", Platform, "Olaylar", "zap", "platform/olaylar", 50, PlatformOnly: true),
    ];
}
