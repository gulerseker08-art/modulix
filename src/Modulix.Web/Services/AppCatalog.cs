using Modulix.Shared.Access;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Orders;
using Modulix.Shared.Support;

namespace Modulix.Web.Services;

public enum AppTone
{
    Indigo,
    Teal,
}

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
    AppTone Tone = AppTone.Indigo);

/// <summary>
/// Projedeki gerçek modül ve ekranların tanımı. Yeni modül ya da ekran eklemek bu listeye satır eklemektir; panel ve
/// sol menü başka yerde sabit yazılmaz. Ekranı olmayan uygulama (ör. Lojistik, CRM'in Leadler/Teklifler/Takvim/Raporlar'ı)
/// buraya eklenmez.
/// </summary>
public static class AppCatalog
{
    public const string Crm = "crm";
    public const string Contracts = "contracts";
    public const string Orders = "orders";
    public const string Support = "support";
    public const string Admin = "admin";
    public const string Platform = "platform";

    public static IReadOnlyList<AppDefinition> All { get; } =
    [
        new(Crm, null, "CRM", "users", null, 10, ModuleKey: CrmAccess.ModuleKey),
        new("crm.leads", Crm, "Lead'ler", "inbox", "crm/leadler", 5, CrmAccess.ModuleKey, CrmAccess.LeadsRead),
        new("crm.companies", Crm, "Firmalar", "building", "crm/firmalar", 10, CrmAccess.ModuleKey, CrmAccess.CompaniesRead),
        new("crm.contacts", Crm, "Kişiler", "user", "crm/kisiler", 20, CrmAccess.ModuleKey, CrmAccess.ContactsRead, Tone: AppTone.Teal),
        new("crm.opportunities", Crm, "Fırsatlar", "target", "crm/firsatlar", 30, CrmAccess.ModuleKey, CrmAccess.OpportunitiesRead),
        new("crm.products", Crm, "Ürün ve hizmetler", "package", "crm/urunler", 35, CrmAccess.ModuleKey, CrmAccess.ProductsRead),
        new("crm.product-categories", Crm, "Ürün kategorileri", "layers", "crm/urun-kategorileri", 35, CrmAccess.ModuleKey, CrmAccess.ProductsRead),
        new("crm.units", Crm, "Ölçü birimleri", "sliders", "crm/birimler", 35, CrmAccess.ModuleKey, CrmAccess.ProductsRead),
        new("crm.price-lists", Crm, "Fiyat listeleri", "list", "crm/fiyat-listeleri", 36, CrmAccess.ModuleKey, CrmAccess.PricesRead, Tone: AppTone.Teal),
        new("crm.tax-rates", Crm, "Vergi ve kur", "landmark", "crm/vergi-kur", 37, CrmAccess.ModuleKey, CrmAccess.PricesRead),
        new("crm.discounts", Crm, "İndirimler", "zap", "crm/indirimler", 38, CrmAccess.ModuleKey, CrmAccess.PricesRead, Tone: AppTone.Teal),
        new("crm.price-calculator", Crm, "Fiyat hesapla", "search", "crm/fiyat-hesapla", 39, CrmAccess.ModuleKey, CrmAccess.PricesRead),
        new("crm.quotes", Crm, "Teklifler", "file-text", "crm/teklifler", 32, CrmAccess.ModuleKey, CrmAccess.QuotesRead),
        new("crm.quote-approvals", Crm, "Teklif onayları", "check-circle", "crm/teklif-onaylari", 33, CrmAccess.ModuleKey, CrmAccess.QuotesApprove, Tone: AppTone.Teal),
        new("crm.quote-rules", Crm, "Teklif kuralları", "shield", "crm/teklif-kurallari", 34, CrmAccess.ModuleKey, CrmAccess.QuoteRulesManage),
        new("crm.quote-delegations", Crm, "Onay vekâletleri", "users", "crm/onay-vekaletleri", 34, CrmAccess.ModuleKey, CrmAccess.QuoteRulesManage),
        new("crm.activities", Crm, "Aktiviteler", "activity", "crm/aktiviteler", 40, CrmAccess.ModuleKey, CrmAccess.ActivitiesRead, Tone: AppTone.Teal),

        new(Contracts, null, "Sözleşmeler", "file-text", null, 20, ModuleKey: ContractsAccess.ModuleKey, Tone: AppTone.Teal),
        new("contracts.dashboard", Contracts, "Pano", "dashboard", "sozlesmeler/ozet", 5, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, ExactMatch: true),
        new("contracts.list", Contracts, "Sözleşmeler", "file-text", "sozlesmeler", 10, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead),
        new("contracts.templates", Contracts, "Şablonlar", "layers", "sozlesmeler/sablonlar", 20, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead),
        new("contracts.approvals", Contracts, "Onaylar", "check-circle", "sozlesmeler/onaylar", 30, ContractsAccess.ModuleKey, ContractsAccess.ContractsApprove, Tone: AppTone.Teal),
        new("contracts.signatures", Contracts, "İmzalar", "check-circle", "sozlesmeler/imzalar", 35, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead),
        new("contracts.commitments", Contracts, "Taahhütler", "clipboard-list", "sozlesmeler/taahhutler", 36, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead, Tone: AppTone.Teal),
        new("contracts.renewals", Contracts, "Yenilemeler", "calendar", "sozlesmeler/yenileme", 37, ContractsAccess.ModuleKey, ContractsAccess.ContractsRead),
        new("contracts.settings", Contracts, "Sözleşme ayarları", "wrench", "yonetim/ayarlar?grup=S%C3%B6zle%C5%9Fmeler", 90, ContractsAccess.ModuleKey, CoreAccess.ManageSettings),
        new("contracts.approval-rules", Contracts, "Onay kuralları", "shield", "sozlesmeler/onay-kurallari", 40, ContractsAccess.ModuleKey, ContractsAccess.ApprovalsManage),

        new(Orders, null, "Siparişler", "package", null, 25, ModuleKey: OrdersAccess.ModuleKey),
        new("orders.dashboard", Orders, "Pano", "dashboard", "siparisler/ozet", 5, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead, ExactMatch: true),
        new("orders.list", Orders, "Siparişler", "package", "siparisler", 10, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead),
        new("orders.jobs", Orders, "Operasyon işleri", "clipboard-list", "siparisler/isler", 20, OrdersAccess.ModuleKey, OrdersAccess.OrdersRead, Tone: AppTone.Teal),

        new(Support, null, "Destek", "message-square", null, 27, ModuleKey: SupportAccess.ModuleKey, Tone: AppTone.Teal),
        new("support.tickets", Support, "Talepler", "inbox", "destek/talepler", 10, SupportAccess.ModuleKey, SupportAccess.TicketsRead, ExactMatch: true),
        new("support.new", Support, "Yeni talep", "clipboard-list", "destek/talepler/yeni", 20, SupportAccess.ModuleKey, SupportAccess.TicketsCreate, ExactMatch: true),
        new("support.settings", Support, "Kuyruklar ve kategoriler", "wrench", "destek/ayarlar", 90, SupportAccess.ModuleKey, SupportAccess.SettingsManage),

        new(Admin, null, "Yönetim", "sliders", null, 90, AnyPermission: CoreAccess.AdministrationPermissions, Tone: AppTone.Teal),
        new("admin.home", Admin, "Genel bakış", "dashboard", "yonetim", 10, AnyPermission: CoreAccess.AdministrationPermissions, ExactMatch: true),
        new("admin.users", Admin, "Kullanıcılar", "user-check", "yonetim/kullanicilar", 20, Permission: CoreAccess.ManageUsers, Tone: AppTone.Teal),
        new("admin.teams", Admin, "Ekipler", "users", "yonetim/ekipler", 30, Permission: CoreAccess.ManageUsers),
        new("admin.roles", Admin, "Roller", "key", "yonetim/roller", 40, Permission: CoreAccess.ManageRoles, Tone: AppTone.Teal),
        new("admin.companies", Admin, "Şirketler", "landmark", "yonetim/sirketler", 50, Permission: CoreAccess.ManageCompanies),
        new("admin.settings", Admin, "Firma ayarları", "wrench", "yonetim/ayarlar", 60, Permission: CoreAccess.ManageSettings, Tone: AppTone.Teal),
        new("admin.audit", Admin, "Denetim kaydı", "history", "yonetim/denetim", 70, Permission: CoreAccess.ReadAudit),
        new("admin.custom-fields", Admin, "Özel alanlar", "square-pen", "yonetim/ozel-alanlar", 80, Permission: CoreAccess.ManageCustomization, Tone: AppTone.Teal),
        new("admin.lists", Admin, "Listeler", "list", "yonetim/listeler", 90, Permission: CoreAccess.ManageCustomization),
        new("admin.sector-profile", Admin, "Sektör profili", "layers", "yonetim/sektor-profili", 100, Permission: CoreAccess.ManageCustomization, Tone: AppTone.Teal),
        new("admin.form-designer", Admin, "Form tasarımı", "clipboard-list", "yonetim/form-tasarimi", 85, Permission: CoreAccess.ManageCustomization),

        new(Platform, null, "Platform", "shield", null, 100, PlatformOnly: true),
        new("platform.home", Platform, "Genel bakış", "dashboard", "platform", 10, PlatformOnly: true, ExactMatch: true),
        new("platform.tenants", Platform, "Firmalar", "building", "platform/firmalar", 20, PlatformOnly: true, Tone: AppTone.Teal),
        new("platform.modules", Platform, "Modül hakları", "toggle", "platform/moduller", 30, PlatformOnly: true),
        new("platform.catalog", Platform, "Modül kataloğu", "package", "platform/katalog", 40, PlatformOnly: true, Tone: AppTone.Teal),
        new("platform.events", Platform, "Olaylar", "zap", "platform/olaylar", 50, PlatformOnly: true),
    ];
}
