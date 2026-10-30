using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Leads;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Pricing;
using Modulix.Core.Contracts.Sales;
using Modulix.Modules.Crm.Quotes;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM: firma, kişi, fırsat, aktivite. İlk sektör modülü ve diğerlerinin şablonu:
/// <list type="bullet">
///   <item>Entity'ler <c>Domain</c>, tablo yapılandırması <c>Persistence</c> (şema = modül anahtarı).</item>
///   <item>Her entity için bir kayıt deposu <c>Records</c>; erişim yalnızca kayıt boru hattından geçer.</item>
///   <item>Uç noktalar ve ekranlar modülü değil, yalnızca bu sınıftaki anahtarları ve izinleri bilir.</item>
/// </list>
/// </summary>
public sealed class CrmModule : IModule
{
    public const string ModuleKey = "crm";

    public static class Permissions
    {
        public const string CompaniesRead = "crm.companies.read";
        public const string CompaniesWrite = "crm.companies.write";
        public const string ContactsRead = "crm.contacts.read";
        public const string ContactsWrite = "crm.contacts.write";

        /// <summary>Firmaları / kişileri birleştirir (ADR-0013). Yazma izninden ayrıdır; hiçbir hazır role verilmez.</summary>
        public const string CompaniesMerge = "crm.companies.merge";
        public const string ContactsMerge = "crm.contacts.merge";
        public const string OpportunitiesRead = "crm.opportunities.read";
        public const string OpportunitiesWrite = "crm.opportunities.write";

        // Fırsat yaşam döngüsü (ADR-0017). Aşama değiştirme/bekletme ve kapatma (kazan/kaybet/iptal), mevcut rollerin davranışı
        // bozulmasın diye yazma izniyle de yapılabilir; yeniden açma ve dışa aktarma kesinlikle ayrı izindir ve hiçbir hazır rolde yoktur.
        public const string OpportunitiesChangeStage = "crm.opportunities.change-stage";
        public const string OpportunitiesClose = "crm.opportunities.close";
        public const string OpportunitiesReopen = "crm.opportunities.reopen";
        public const string OpportunitiesExport = "crm.opportunities.export";

        /// <summary>Fırsat tutarı ve olasılığı (Y8b, hassas alan). Olmayan kişi fırsatı görür, bu alanları görmez ve yazamaz.</summary>
        public const string OpportunityAmountsRead = "crm.opportunity-amounts.read";
        public const string ActivitiesRead = "crm.activities.read";
        public const string ActivitiesWrite = "crm.activities.write";

        // Aktivite yaşam döngüsü (ADR-0015). Oluşturma/düzenleme/tamamlama/iptal/silme `write` ile birlikte kalır (mevcut rollerin davranışı
        // değişmesin diye); aşağıdakiler ayrı izinlerdir.

        /// <summary>Aktiviteyi başkasına atar (kendine almak `write` yeter).</summary>
        public const string ActivitiesAssign = "crm.activities.assign";

        /// <summary>Kapanmış (tamamlanmış, iptal, ulaşılamadı, katılım olmadı) aktiviteyi yeniden açar.</summary>
        public const string ActivitiesReopen = "crm.activities.reopen";

        /// <summary>Başkalarının takvimini ve ekip görünümünü görür (yalnız meşguliyet; özel aktivite ayrıntısı değil).</summary>
        public const string ActivitiesManageCalendar = "crm.activities.manage-calendar";

        /// <summary>Özel aktivitelerin ayrıntısını (başlık, açıklama, ilişkiler) kapsamındaysa görür. Hiçbir hazır role verilmez.</summary>
        public const string ActivitiesViewPrivate = "crm.activities.view-private";

        // Lead yönetimi. Atama, nitelendirme, eleme, dönüşüm ve dışa aktarma ayrı izinlerdir; alan düzenleme izninden bağımsızdır.
        public const string LeadsRead = "crm.leads.read";
        public const string LeadsCreate = "crm.leads.create";
        public const string LeadsUpdate = "crm.leads.update";
        public const string LeadsDelete = "crm.leads.delete";
        public const string LeadsAssign = "crm.leads.assign";
        public const string LeadsQualify = "crm.leads.qualify";
        public const string LeadsDisqualify = "crm.leads.disqualify";
        public const string LeadsConvert = "crm.leads.convert";
        public const string LeadsExport = "crm.leads.export";

        /// <summary>Lead toplama formlarını/entegrasyonlarını yönetir (sır yenileme dahil). Hiçbir hazır role verilmez.</summary>
        public const string LeadFormsManage = "crm.lead-forms.manage";

        // Ürün/hizmet kataloğu (ADR-0018). Maliyeti görme ayrı izindir; hiçbir hazır role verilmez.
        public const string ProductsRead = "crm.products.read";
        public const string ProductsWrite = "crm.products.write";
        public const string ProductCostsRead = "crm.product-costs.read";

        // Fiyat listeleri, vergi, kur ve indirim politikaları (ADR-0018). Fiyat hesabı (calculate) okuma iznini ister.
        public const string PricesRead = "crm.prices.read";
        public const string PricesWrite = "crm.prices.write";

        // Teklif yönetimi (ADR-0019). Maliyet, indirim, elle fiyat, sunum, iç onay ve müşteri kararı kaydı ayrı izinlerdir.
        public const string QuotesRead = "crm.quotes.read";
        public const string QuotesWrite = "crm.quotes.write";
        public const string QuotesDiscount = "crm.quotes.discount";
        public const string QuotesManualPrice = "crm.quotes.manual-price";
        public const string QuotesApprove = "crm.quotes.approve";
        public const string QuotesPresent = "crm.quotes.present";
        public const string QuotesRecordDecision = "crm.quotes.record-decision";
        public const string QuotesCancel = "crm.quotes.cancel";
        public const string QuotesShare = "crm.quotes.share";
        public const string QuoteRulesManage = "crm.quote-rules.manage";
        public const string QuotesExport = "crm.quotes.export";
        public const string QuoteCostsRead = "crm.quote-costs.read";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Company = "crm.company";
        public const string Contact = "crm.contact";
        public const string CompanyContact = "crm.company-contact";
        public const string Location = "crm.location";
        public const string Address = "crm.address";
        public const string ContactPoint = "crm.contact-point";
        public const string PersonContactPoint = "crm.person-contact-point";
        public const string CompanyRelation = "crm.company-relation";
        public const string Opportunity = "crm.opportunity";
        public const string OpportunityContact = "crm.opportunity-contact";
        public const string OpportunityCompetitor = "crm.opportunity-competitor";
        public const string Activity = "crm.activity";
        public const string ActivityLink = "crm.activity-link";
        public const string ActivityParticipant = "crm.activity-participant";
        public const string Lead = "crm.lead";
        public const string LeadForm = "crm.lead-form";
        public const string ProductCategory = "crm.product-category";
        public const string MeasureUnit = "crm.measure-unit";
        public const string Product = "crm.product";
        public const string TaxRate = "crm.tax-rate";
        public const string ExchangeRate = "crm.exchange-rate";
        public const string PriceList = "crm.price-list";
        public const string PriceListItem = "crm.price-list-item";
        public const string ProductComponent = "crm.product-component";
        public const string DiscountPolicy = "crm.discount-policy";
        public const string Quote = "crm.quote";
        public const string QuoteLine = "crm.quote-line";
        public const string QuoteApprovalRule = "crm.quote-approval-rule";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string OpportunityStage = "crm.opportunity-stage";
        public const string OpportunityType = "crm.opportunity-type";
        public const string OpportunityLossReason = "crm.opportunity-loss-reason";
        public const string CompanyStatus = "crm.company-status";
        public const string CompanySource = "crm.company-source";
        public const string ActivityType = "crm.activity-type";
        public const string LeadSource = "crm.lead-source";
        public const string LeadDisqualificationReason = "crm.lead-disqualification-reason";
    }

    /// <summary>
    /// Aktivite sonucu. Lead'in "ilk temas"ı yalnızca gerçekleşen temaslardan türetilir: tamamlanmış Toplantı; sonucu
    /// <c>Reached</c> olan tamamlanmış Arama; sonucu <c>Reached</c> ya da <c>Sent</c> olan tamamlanmış E-posta. Görev, Not ve
    /// sonucu boş/<c>NoAnswer</c> olan aramalar temas sayılmaz; "arama planlandı" temas değildir.
    /// </summary>
    public static class ActivityOutcomes
    {
        public const string Reached = "Reached";
        public const string NoAnswer = "NoAnswer";
        public const string Sent = "Sent";

        public static IReadOnlyList<string> All { get; } = [Reached, NoAnswer, Sent];
    }

    /// <summary>Fırsat aşamasının kategorileri: kazanılan ve kaybedilen fırsat kapanmıştır (devir, açık fırsat süzgeci).</summary>
    public static class StageCategories
    {
        public const string Open = "open";
        public const string Won = "won";
        public const string Lost = "lost";
    }

    /// <summary>
    /// Kaybetme nedeni kategorileri (ADR-0017). Yalnızca <c>competitor</c> kategorisi "rakibe kaybedildi"dir; vazgeçme ve mevcut
    /// çözümle devam rakibe kayıp sayılmaz. <c>other</c> kategorisinde açıklama zorunludur.
    /// </summary>
    public static class LossCategories
    {
        public const string Competitor = "competitor";
        public const string NoDecision = "no-decision";
        public const string StatusQuo = "status-quo";
        public const string Fit = "fit";
        public const string Other = "other";
    }

    /// <summary>Fırsat–kişi rolleri; adlar Shared'daki <c>OpportunityContactRoles</c> ile aynıdır.</summary>
    public static IReadOnlyList<string> OpportunityContactRoles { get; } =
        ["DecisionMaker", "BudgetOwner", "TechnicalEvaluator", "Purchaser", "EndUser", "Champion", "Other"];

    /// <summary>İlişki rolü anahtarları (ADR-0011); adlar Shared'daki <c>CrmRelationshipRoles</c> ile aynıdır.</summary>
    public static IReadOnlyList<string> RelationshipRoles { get; } = ["Purchasing", "Finance", "Technical", "Management", "Other"];

    /// <summary>Lokasyon türleri (ADR-0012); adlar Shared'daki <c>CrmLocationTypes</c> ile aynıdır.</summary>
    public static IReadOnlyList<string> LocationTypes { get; } = ["HeadOffice", "Branch", "Plant", "Warehouse", "Store", "Delivery", "Billing", "Other"];

    public static IReadOnlyList<string> AddressTypes { get; } = ["Registered", "Billing", "Shipping", "Office", "Other"];

    public static IReadOnlyList<string> ContactPointTypes { get; } = ["Work", "Mobile", "Switchboard", "Billing", "Support", "Personal", "Other"];

    public static IReadOnlyList<string> ContactChannels { get; } = ["Phone", "Email"];

    public static IReadOnlyList<string> RelationTypes { get; } = ["Subsidiary", "Affiliate", "Other"];

    /// <summary>Firma durumunun kategorileri.</summary>
    public static class CompanyStatusCategories
    {
        public const string Prospect = "prospect";
        public const string Customer = "customer";
        public const string Inactive = "inactive";
    }

    public string Key => ModuleKey;

    public string DisplayName => "CRM";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.CompaniesRead, "Firmaları görüntüler", "Firmalar"),
        new(Permissions.CompaniesWrite, "Firma ekler, düzenler ve siler", "Firmalar"),
        new(Permissions.ContactsRead, "Kişileri görüntüler", "Kişiler"),
        new(Permissions.ContactsWrite, "Kişi ekler, düzenler ve siler", "Kişiler"),
        new(Permissions.CompaniesMerge, "Mükerrer firmaları birleştirir", "Firmalar"),
        new(Permissions.ContactsMerge, "Mükerrer kişileri birleştirir", "Kişiler"),
        new(Permissions.OpportunitiesRead, "Fırsatları görüntüler", "Fırsatlar"),
        new(Permissions.OpportunitiesWrite, "Fırsat ekler, düzenler ve siler", "Fırsatlar"),
        new(Permissions.OpportunitiesChangeStage, "Fırsatın aşamasını değiştirir, beklemeye alır ve çıkarır", "Fırsatlar"),
        new(Permissions.OpportunitiesClose, "Fırsatı kazanıldı, kaybedildi ya da iptal olarak kapatır", "Fırsatlar"),
        new(Permissions.OpportunitiesReopen, "Kapanmış fırsatı gerekçeyle yeniden açar", "Fırsatlar"),
        new(Permissions.OpportunitiesExport, "Fırsat listesini dışa aktarır", "Fırsatlar"),
        new(Permissions.OpportunityAmountsRead, "Fırsat tutarını ve olasılığını görür", "Fırsat tutarları"),
        new(Permissions.ActivitiesRead, "Aktiviteleri görüntüler", "Aktiviteler"),
        new(Permissions.ActivitiesWrite, "Aktivite ekler, düzenler, tamamlar, iptal eder ve siler", "Aktiviteler"),
        new(Permissions.ActivitiesAssign, "Aktiviteyi başka kullanıcıya atar", "Aktiviteler"),
        new(Permissions.ActivitiesReopen, "Kapanmış aktiviteyi yeniden açar", "Aktiviteler"),
        new(Permissions.ActivitiesManageCalendar, "Ekip takvimini ve başkalarının meşguliyetini görür", "Aktiviteler"),
        new(Permissions.ActivitiesViewPrivate, "Özel aktivitelerin ayrıntısını görür", "Aktiviteler"),
        new(Permissions.LeadsRead, "Lead'leri görüntüler", "Lead'ler"),
        new(Permissions.LeadsCreate, "Lead oluşturur", "Lead'ler"),
        new(Permissions.LeadsUpdate, "Lead alanlarını düzenler", "Lead'ler"),
        new(Permissions.LeadsDelete, "Lead siler", "Lead'ler"),
        new(Permissions.LeadsAssign, "Lead atar ve atama bekleyen Lead'leri görür", "Lead'ler"),
        new(Permissions.LeadsQualify, "Lead'i nitelendirir ve daha sonra takibe alır", "Lead'ler"),
        new(Permissions.LeadsDisqualify, "Lead'i eler ve elenen Lead'i yeniden açar", "Lead'ler"),
        new(Permissions.LeadsConvert, "Nitelikli Lead'i fırsata dönüştürür", "Lead'ler"),
        new(Permissions.LeadsExport, "Lead listesini dışa aktarır", "Lead'ler"),
        new(Permissions.LeadFormsManage, "Lead toplama formlarını ve entegrasyon sırlarını yönetir", "Lead formları"),
        new(Permissions.ProductsRead, "Ürün, hizmet, kategori ve birimleri görüntüler", "Ürün kataloğu"),
        new(Permissions.ProductsWrite, "Ürün, hizmet, kategori ve birim ekler, düzenler", "Ürün kataloğu"),
        new(Permissions.ProductCostsRead, "Ürün maliyetini görür ve düzenler", "Ürün maliyeti"),
        new(Permissions.PricesRead, "Fiyat listelerini, vergi oranlarını, kurları ve indirim politikalarını görüntüler; fiyat hesaplar", "Fiyatlandırma"),
        new(Permissions.PricesWrite, "Fiyat listesi, vergi oranı, kur ve indirim politikası ekler, düzenler, yayımlar", "Fiyatlandırma"),
        new(Permissions.QuotesRead, "Teklifleri görüntüler", "Teklifler"),
        new(Permissions.QuotesWrite, "Teklif ve kalem ekler, düzenler, revizyon açar, çoğaltır, onaya gönderir", "Teklifler"),
        new(Permissions.QuotesDiscount, "Tekliflerde satır ve genel indirim uygular", "Teklifler"),
        new(Permissions.QuotesManualPrice, "Tekliflerde elle birim fiyat girer ve serbest kalem ekler", "Teklifler"),
        new(Permissions.QuotesApprove, "İç onay bekleyen teklifi onaylar, reddeder ya da değişiklik ister", "Teklifler"),
        new(Permissions.QuotesPresent, "Onaylı teklifi müşteriye sunar", "Teklifler"),
        new(Permissions.QuotesRecordDecision, "Müşterinin kabul ya da ret kararını kaydeder", "Teklifler"),
        new(Permissions.QuotesCancel, "Teklifi gerekçeyle iptal eder", "Teklifler"),
        new(Permissions.QuotesShare, "Sunulmuş teklif için müşteri belgesi (PDF) ve paylaşım bağlantısı üretir, bağlantıyı iptal eder", "Teklifler"),
        new(Permissions.QuoteRulesManage, "Teklif iç onay kurallarını (indirim/tutar eşiği, onaylayan) tanımlar ve değiştirir", "Teklifler"),
        new(Permissions.QuotesExport, "Teklif listesini CSV olarak dışa aktarır (maliyet ve iç not hariç)", "Teklifler"),
        new(Permissions.QuoteCostsRead, "Teklif kalemlerinin maliyetini görür ve düzenler", "Teklif maliyeti"),
    ];

    /// <summary>Devre dışı bırakılan kişinin firmaları, açık fırsatları ve açık aktiviteleri devredilir.</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Company, "firma") { Path = "crm/firmalar/{id}" },
        // Yürüyen (açık ve beklemedeki) fırsatlar devredilir; kazanılan/kaybedilen/iptal edilen geçmiş olarak eski sorumlusunda kalır.
        new(Entities.Opportunity, "açık fırsat", new Dictionary<string, string> { ["live"] = "true" }) { Path = "crm/firsatlar/{id}" },
        new(Entities.Activity, "açık aktivite", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/aktiviteler" },
        new(Entities.Lead, "açık lead", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/leadler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "crm.sales-rep",
            "Satış temsilcisi",
            "Firmaları, kişileri, fırsatları ve aktiviteleri görüntüler ve düzenler.",
            [
                Permissions.CompaniesRead, Permissions.CompaniesWrite,
                Permissions.ContactsRead, Permissions.ContactsWrite,
                Permissions.OpportunitiesRead, Permissions.OpportunitiesWrite, Permissions.OpportunityAmountsRead,
                Permissions.ProductsRead, Permissions.PricesRead, Permissions.QuotesRead, Permissions.QuotesWrite,
                Permissions.OpportunitiesChangeStage, Permissions.OpportunitiesClose,
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite, Permissions.ActivitiesReopen,
                Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadsUpdate,
                Permissions.LeadsQualify, Permissions.LeadsDisqualify, Permissions.LeadsConvert,
            ])
        {
            // Y8: temsilci yalnızca sorumlusu olduğu kayıtları görür; ekip lideri için kapsam rolde genişletilir.
            Scope = RecordScope.Own,
        },
        // Lead dağıtımı: atama bekleyen havuzu görür, Lead açar ve atar; nitelendirme/dönüşüm satış tarafındadır.
        new(
            "crm.lead-coordinator",
            "Lead koordinatörü",
            "Lead'leri toplar, atama bekleyenleri sorumlulara dağıtır ve raporlar.",
            [
                Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadsUpdate, Permissions.LeadsAssign, Permissions.LeadsExport,
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite, Permissions.ActivitiesAssign, Permissions.ActivitiesReopen,
                Permissions.ActivitiesManageCalendar,
            ])
        {
            Scope = RecordScope.All,
        },
    ];

    /// <summary>
    /// Hassas alanlar (Y8b): tutar ve olasılık. Para birimi tek başına bir şey söylemediği için hassas değildir (ekran onu
    /// her zaman gönderir).
    /// </summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Opportunity, Permissions.OpportunityAmountsRead, ["amount", "probability", "customerBudget", "wonAmount"]),
        new(Entities.Product, Permissions.ProductCostsRead, ["cost", "costCurrency"]),
        new(Entities.QuoteLine, Permissions.QuoteCostsRead, ["unitCost"]),
    ];

    /// <summary>Özel alanlar (Y9a): firma dört kayıt türüne de kendi alanlarını ekleyebilir.</summary>
    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Company, "Firma"),
        new(Entities.Contact, "Kişi"),
        new(Entities.Opportunity, "Fırsat"),
        new(Entities.Activity, "Aktivite"),
        new(Entities.Lead, "Lead"),
    ];

    /// <summary>Lookup alanları (ADR-0016); tanımlar <see cref="CrmLookupDefinitions"/>'tadır.</summary>
    IReadOnlyList<LookupDefinition> IModule.Lookups => CrmLookupDefinitions.All;

    /// <summary>
    /// Seçim listeleri (Y9b). Hazır değerlerin anahtarları eski enum adlarıdır; kayıtlarda ve API'de bunlar durur.
    /// Firma adlarını, sırasını ve etkinliğini değiştirir; yeni değer eklerken kategorisini seçer.
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.OpportunityStage,
            "Fırsat aşamaları",
            Entities.Opportunity,
            "stage",
            Required: true,
            [new(StageCategories.Open, "Açık"), new(StageCategories.Won, "Kazanıldı"), new(StageCategories.Lost, "Kaybedildi")],
            [
                new("Lead", "Aday", StageCategories.Open),
                new("Qualified", "Nitelikli", StageCategories.Open),
                new("Proposal", "Teklif", StageCategories.Open),
                new("Negotiation", "Pazarlık", StageCategories.Open),
                new("Won", "Kazanıldı", StageCategories.Won),
                new("Lost", "Kaybedildi", StageCategories.Lost),
            ]),
        new(
            Lists.OpportunityType,
            "Fırsat türü",
            Entities.Opportunity,
            "type",
            Required: false,
            [],
            [new("NewCustomer", "Yeni müşteri"), new("Upsell", "Mevcut müşteriye ek satış"), new("Renewal", "Yenileme"), new("Other", "Diğer")]),
        new(
            Lists.OpportunityLossReason,
            "Fırsat kaybetme nedeni",
            Entities.Opportunity,
            "lossReason",
            Required: false,
            // Kategori kaybın niteliğini belirler: yalnızca "competitor" rakibe kayıptır; "other" açıklama ister.
            [
                new(LossCategories.Competitor, "Rakibe kaybedildi"), new(LossCategories.NoDecision, "Satın almaktan vazgeçti"),
                new(LossCategories.StatusQuo, "Mevcut çözümle devam etti"), new(LossCategories.Fit, "Fiyat / bütçe / ürün uyumu"),
                new(LossCategories.Other, "Diğer"),
            ],
            [
                new("Competitor", "Rakibi seçti", LossCategories.Competitor),
                new("NoDecision", "Satın almaktan vazgeçti", LossCategories.NoDecision),
                new("StatusQuo", "Mevcut çözümle devam etti", LossCategories.StatusQuo),
                new("Price", "Fiyat", LossCategories.Fit), new("Budget", "Bütçe", LossCategories.Fit),
                new("ProductFit", "Ürün uyumu", LossCategories.Fit), new("Timing", "Zamanlama", LossCategories.Fit),
                new("Other", "Diğer", LossCategories.Other),
            ]),
        new(
            Lists.CompanyStatus,
            "Firma durumu",
            Entities.Company,
            "status",
            Required: true,
            [
                new(CompanyStatusCategories.Prospect, "Potansiyel"),
                new(CompanyStatusCategories.Customer, "Müşteri"),
                new(CompanyStatusCategories.Inactive, "Pasif"),
            ],
            [
                new("Prospect", "Potansiyel", CompanyStatusCategories.Prospect),
                new("Customer", "Müşteri", CompanyStatusCategories.Customer),
                new("Inactive", "Pasif", CompanyStatusCategories.Inactive),
            ]),
        new(
            Lists.CompanySource,
            "Firma kaynağı",
            Entities.Company,
            "source",
            Required: false,
            [],
            [
                new("Referral", "Tavsiye"), new("Website", "Web sitesi"), new("Event", "Etkinlik / fuar"),
                new("Advertising", "Reklam"), new("ColdCall", "Soğuk arama"), new("Other", "Diğer"),
            ]),
        new(
            Lists.ActivityType,
            "Aktivite tipi",
            Entities.Activity,
            "type",
            Required: true,
            // Kategori = aktivite ailesi (durum makinesi, ADR-0015). Kategorisiz değer görev gibi davranır; firma yeni tür eklerken aile seçer.
            [
                new(ActivityFamily.Task, "Görev"), new(ActivityFamily.Call, "Arama"), new(ActivityFamily.Meeting, "Toplantı"),
                new(ActivityFamily.Visit, "Müşteri / saha ziyareti"), new(ActivityFamily.Email, "E-posta"),
            ],
            [
                new("Call", "Arama", ActivityFamily.Call), new("Meeting", "Toplantı", ActivityFamily.Meeting),
                new("Email", "E-posta", ActivityFamily.Email), new("Task", "Görev", ActivityFamily.Task), new("Note", "Not", ActivityFamily.Task),
                new("Visit", "Ziyaret", ActivityFamily.Visit), new("FollowUp", "Takip", ActivityFamily.Task),
            ])
        {
            Default = "Task",
        },
        new(
            Lists.LeadSource,
            "Lead kaynağı",
            Entities.Lead,
            "source",
            Required: false,
            [],
            [
                new("Manual", "Elle giriş"), new("WebForm", "Web formu"), new("Referral", "Tavsiye"), new("Event", "Etkinlik / fuar"),
                new("Advertising", "Reklam"), new("Phone", "Telefon"), new("Email", "E-posta"), new("Other", "Diğer"),
            ]),
        new(
            Lists.LeadDisqualificationReason,
            "Lead eleme nedeni",
            Entities.Lead,
            "disqualificationReason",
            Required: false,
            [],
            [
                new("NoBudget", "Bütçe yok"), new("NoNeed", "İhtiyaç yok"), new("NoResponse", "Ulaşılamadı"), new("Competitor", "Rakibi seçti"),
                new("NotATarget", "Hedef müşteri değil"), new("Duplicate", "Mükerrer"), new("Spam", "Spam / geçersiz"), new("Other", "Diğer"),
            ]),
    ];

    /// <summary>İlk temas hedefi: Lead'in ilk temasının beklenen en uzun süresi. Çalışma saati takvimi olmadığı için TAKVİM saati cinsindendir.</summary>
    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.LeadFirstContactTargetHours, "İlk temas hedefi (takvim saati)", "CRM Lead", SettingType.Text, "24")
        {
            Description = "Lead'in ilk temasının hedeflenen en uzun süresi, takvim saati olarak (iş saati değildir). Boş bırakılırsa hedef uygulanmaz.",
            Pattern = @"^[1-9][0-9]{0,3}$",
            PatternMessage = "1-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        // Fırsat politikası (ADR-0017). Modül ayarları yönetim ekranından düzenlenir; geçersiz biçim kaydedilmez.
        new(SettingKeys.OpportunityStageRequirements, "Fırsat aşama koşulları", "CRM Fırsat", SettingType.Text,
            "Proposal=amount|expectedCloseDate;Negotiation=amount|expectedCloseDate|contactId")
        {
            Description = "Bir aşamaya geçerken dolu olması gereken alanlar. Biçim: Aşama=alan|alan;Aşama=alan (ör. Proposal=amount|expectedCloseDate). Boş bırakılırsa koşul uygulanmaz.",
            Pattern = @"^$|^[A-Za-z][A-Za-z0-9_.-]*=[A-Za-z]+(\|[A-Za-z]+)*(;[A-Za-z][A-Za-z0-9_.-]*=[A-Za-z]+(\|[A-Za-z]+)*)*$",
            PatternMessage = "Biçim: Aşama=alan|alan;Aşama=alan",
            MaxLength = 400,
        },
        new(SettingKeys.OpportunityStageProbabilities, "Fırsat aşama olasılıkları (%)", "CRM Fırsat", SettingType.Text,
            "Lead=10;Qualified=25;Proposal=50;Negotiation=75")
        {
            Description = "Aşamanın varsayılan kazanma olasılığı. Biçim: Aşama=yüzde;Aşama=yüzde. Kullanıcı olasılığı elle girdiyse aşama değişince ezilmez; kazanılanda 100, kaybedilen ve iptalde 0 uygulanır.",
            Pattern = @"^$|^[A-Za-z][A-Za-z0-9_.-]*=(100|[0-9]{1,2})(;[A-Za-z][A-Za-z0-9_.-]*=(100|[0-9]{1,2}))*$",
            PatternMessage = "Biçim: Aşama=yüzde;Aşama=yüzde (0-100)",
            MaxLength = 400,
        },
        new(SettingKeys.OpportunityStaleDays, "Fırsat hareketsizlik eşiği (gün)", "CRM Fırsat", SettingType.Text, "30")
        {
            Description = "Son müşteri etkileşiminden bu kadar gün geçen açık fırsat hareketsiz sayılır.",
            Pattern = @"^[1-9][0-9]{0,3}$",
            PatternMessage = "1-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        new(SettingKeys.QuoteApprovalDiscountPercent, "Teklif iç onay: indirim eşiği (%)", "CRM Teklif", SettingType.Text)
        {
            Description = "Toplam indirim bu yüzdeyi aşan teklif iç onay almadan müşteriye sunulamaz. Boş bırakılırsa indirimden onay istenmez.",
            Pattern = @"^$|^(100|[0-9]{1,2})(.[0-9]{1,2})?$",
            PatternMessage = "0-100 arası bir yüzde girin.",
            MaxLength = 6,
        },
        new(SettingKeys.QuoteApprovalGrossAbove, "Teklif iç onay: toplam tutar eşiği", "CRM Teklif", SettingType.Text)
        {
            Description = "Genel toplamı bu tutarı aşan teklif (teklifin kendi para biriminde) iç onay almadan sunulamaz. Boş bırakılırsa tutardan onay istenmez.",
            Pattern = @"^$|^[0-9]{1,13}(.[0-9]{1,2})?$",
            PatternMessage = "Negatif olmayan bir tutar girin.",
            MaxLength = 16,
        },
        new(SettingKeys.QuoteDefaultValidityDays, "Teklif varsayılan geçerlilik süresi (gün)", "CRM Teklif", SettingType.Text, "30")
        {
            Description = "Yeni tekliflerde önerilen geçerlilik süresi.",
            Pattern = @"^[1-9][0-9]{0,3}$",
            PatternMessage = "1-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        new(SettingKeys.QuoteAllowSelfApproval, "Teklif iç onay: kendi gönderdiğini onaylama", "CRM Teklif", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse teklifi onaya gönderen kişi onaylayan yetkisine sahipse kendi teklifini onaylayabilir. Varsayılan hayırdır.",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.QuoteOnAcceptWinOpportunity, "Teklif kabulünde fırsatı kazanıldı yap", "CRM Teklif", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse müşteri bir teklifi kabul edince (ya da kabul kaydedilince) teklifin bağlı olduğu açık fırsat kazanıldı olarak kapanır; kazanılan tutar teklifin net toplamıdır. Varsayılan hayırdır.",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.QuoteFollowUpDays, "Teklif sunumundan sonra takip aktivitesi (gün)", "CRM Teklif", SettingType.Text, "0")
        {
            Description = "Teklif müşteriye sunulunca bu kadar gün sonrasına teklif sorumlusu için takip aktivitesi açılır. 0 kapalıdır.",
            Pattern = @"^[0-9]{1,3}$",
            PatternMessage = "0-999 arası tam sayı girin.",
            MaxLength = 3,
        },
        new(SettingKeys.QuoteExpiryReminderDays, "Teklif süresi dolmadan hatırlatma (gün)", "CRM Teklif", SettingType.Text, "0")
        {
            Description = "Müşteride bekleyen teklifin geçerliliği bitmeden bu kadar gün önce sorumlu için hatırlatma aktivitesi açılır. 0 kapalıdır.",
            Pattern = @"^[0-9]{1,3}$",
            PatternMessage = "0-999 arası tam sayı girin.",
            MaxLength = 3,
        },
        new(SettingKeys.QuoteSyncOpportunityAmount, "Fırsat tutarını esas tekliften eşitle", "CRM Teklif", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse açık fırsatın tutarı ve para birimi, esas teklifi müşteriye sunulduğunda (ve esas teklif değiştirildiğinde) teklifin net toplamıyla eşitlenir. Varsayılan hayırdır; fırsat tutarı elle girilir.",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.QuoteSellerDetails, "Teklif belgesi: satıcı bilgileri", "CRM Teklif", SettingType.Text)
        {
            Description = "Müşteri teklif belgesinin (PDF) başlığında firma adının altında görünür: adres, telefon, vergi dairesi/numarası gibi. Boş bırakılabilir.",
            MaxLength = 300,
        },
        new(SettingKeys.QuoteFooterNote, "Teklif belgesi: alt bilgi notu", "CRM Teklif", SettingType.Text)
        {
            Description = "Müşteri teklif belgesinin her sayfasının altında görünen kısa not. Boş bırakılabilir.",
            MaxLength = 200,
        },
    ];

    /// <summary>Denetim kaydı (Y5): alanların görünen adları. Liste alanlarının değer adları firmanın listesinden gelir (Y9b).</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Company, "Firma (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["kind"] = "Tür", ["taxOffice"] = "Vergi dairesi", ["taxNumber"] = "Vergi no", ["phone"] = "Telefon", ["email"] = "E-posta", ["website"] = "Web sitesi",
            ["city"] = "Şehir", ["address"] = "Adres", ["industry"] = "Sektör", ["status"] = "Durum", ["source"] = "Kaynak",
            ["tags"] = "Etiketler", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar", ["partyId"] = "Ortak firma kaydı",
        }, "name")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party },
        },
        new(Entities.Contact, "Kişi (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["firstName"] = "Ad", ["lastName"] = "Soyad", ["companyId"] = "Firma", ["title"] = "Unvan", ["email"] = "E-posta",
            ["phone"] = "Telefon", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar", ["personId"] = "Ortak kişi kaydı",
        })
        {
            TitleFields = ["firstName", "lastName"],
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["personId"] = SharedEntities.Person },
        },
        new(Entities.CompanyContact, "Firma–kişi ilişkisi (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["contactId"] = "Kişi", ["companyName"] = "Firma adı", ["contactName"] = "Kişi adı",
            ["department"] = "Departman", ["jobTitle"] = "Unvan", ["roles"] = "İlişki rolleri", ["isPrimary"] = "Ana irtibat",
            ["isDecisionMaker"] = "Karar verici", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş", ["isActive"] = "Aktif",
        })
        {
            TitleFields = ["contactName", "companyName"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Location, "Müşteri lokasyonu (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["companyName"] = "Firma adı", ["name"] = "Ad", ["code"] = "Kod", ["type"] = "Tür",
            ["isActive"] = "Aktif", ["contactId"] = "Yetkili kişi", ["notes"] = "Notlar",
        }, "name")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Address, "Adres (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["locationId"] = "Lokasyon", ["type"] = "Tür", ["isPrimary"] = "Ana adres", ["isActive"] = "Aktif",
            ["country"] = "Ülke", ["city"] = "Şehir", ["district"] = "İlçe", ["postalCode"] = "Posta kodu", ["line1"] = "Adres satırı 1",
            ["line2"] = "Adres satırı 2", ["normalizedText"] = "Arama metni",
        }, "line1")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["locationId"] = Entities.Location },
        },
        new(Entities.ContactPoint, "Firma iletişim noktası (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["locationId"] = "Lokasyon", ["channel"] = "Kanal", ["type"] = "Tür", ["value"] = "Değer",
            ["normalizedValue"] = "Normalleştirilmiş değer", ["isPrimary"] = "Ana", ["isActive"] = "Aktif", ["isShared"] = "Paylaşılan", ["note"] = "Not",
        }, "value")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["locationId"] = Entities.Location },
        },
        new(Entities.PersonContactPoint, "Kişi iletişim noktası (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["contactId"] = "Kişi", ["companyContactId"] = "Firma–kişi ilişkisi", ["channel"] = "Kanal", ["type"] = "Tür", ["value"] = "Değer",
            ["normalizedValue"] = "Normalleştirilmiş değer", ["isPrimary"] = "Ana", ["isActive"] = "Aktif", ["isShared"] = "Paylaşılan", ["note"] = "Not",
        }, "value")
        {
            References = new Dictionary<string, string> { ["contactId"] = Entities.Contact, ["companyContactId"] = Entities.CompanyContact },
        },
        new(Entities.CompanyRelation, "Firma–firma ilişkisi (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["parentCompanyId"] = "Ana firma", ["childCompanyId"] = "Bağlı firma", ["parentCompanyName"] = "Ana firma adı",
            ["childCompanyName"] = "Bağlı firma adı", ["type"] = "Tür", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş",
            ["isActive"] = "Aktif", ["notes"] = "Notlar",
        })
        {
            TitleFields = ["parentCompanyName", "childCompanyName"],
            References = new Dictionary<string, string> { ["parentCompanyId"] = Entities.Company, ["childCompanyId"] = Entities.Company },
        },
        new(Entities.Opportunity, "Fırsat (CRM)", Permissions.OpportunitiesRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["companyId"] = "Firma", ["contactId"] = "Kişi", ["stage"] = "Aşama", ["amount"] = "Tutar",
            ["currency"] = "Para birimi", ["expectedCloseDate"] = "Tahmini kapanış", ["probability"] = "Olasılık (%)",
            ["lostReason"] = "Kaybetme açıklaması", ["sourceLeadId"] = "Kaynak Lead", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
            ["opportunityNumber"] = "Fırsat no", ["description"] = "Açıklama", ["status"] = "Durum", ["statusReason"] = "Durum gerekçesi",
            ["priority"] = "Öncelik", ["type"] = "Tür", ["source"] = "Kaynak", ["customerBudget"] = "Müşteri bütçesi",
            ["budgetStatus"] = "Bütçe durumu", ["needDescription"] = "İhtiyaç", ["currentProblem"] = "Mevcut sorun",
            ["expectedBenefit"] = "Beklenen fayda", ["scope"] = "Kapsam", ["successCriteria"] = "Başarı ölçütleri", ["risks"] = "Riskler",
            ["decisionProcess"] = "Karar süreci", ["targetDecisionDate"] = "Hedef karar tarihi", ["nextAction"] = "Sonraki aksiyon",
            ["nextActionDate"] = "Aksiyon tarihi", ["lastInteractionAt"] = "Son müşteri etkileşimi", ["stageEnteredAt"] = "Aşamaya giriş",
            ["probabilityManual"] = "Olasılık elle girildi", ["actualCloseDate"] = "Gerçek kapanış", ["wonAmount"] = "Kazanılan tutar", ["primaryQuoteId"] = "Esas teklif",
            ["wonCurrency"] = "Kazanılan para birimi", ["winReason"] = "Kazanma nedeni", ["handoverNote"] = "Devir notu",
            ["lossReason"] = "Kaybetme nedeni", ["winningCompetitorId"] = "Kazanan rakip", ["recontactDate"] = "Yeniden iletişim",
            ["holdReviewDate"] = "Yeniden değerlendirme", ["closureCycle"] = "Kapanış döngüsü", ["closeDateChangeCount"] = "Kapanış tarihi değişimi",
        }, "title")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["sourceLeadId"] = Entities.Lead,
                ["winningCompetitorId"] = Entities.OpportunityCompetitor, ["primaryQuoteId"] = Entities.Quote,
            },
        },
        new(Entities.OpportunityContact, "Fırsat kişisi (CRM)", Permissions.OpportunitiesRead, new Dictionary<string, string>
        {
            ["opportunityId"] = "Fırsat", ["contactId"] = "Kişi", ["contactName"] = "Kişi adı", ["roles"] = "Roller", ["isPrimary"] = "Ana irtibat", ["note"] = "Not",
        })
        {
            TitleFields = ["contactName"],
            References = new Dictionary<string, string> { ["opportunityId"] = Entities.Opportunity, ["contactId"] = Entities.Contact },
        },
        new(Entities.OpportunityCompetitor, "Fırsat rakibi (CRM)", Permissions.OpportunitiesRead, new Dictionary<string, string>
        {
            ["opportunityId"] = "Fırsat", ["name"] = "Ad", ["strengths"] = "Güçlü yönler", ["weaknesses"] = "Zayıf yönler",
            ["alternativeAmount"] = "Alternatif teklif tutarı", ["alternativeCurrency"] = "Alternatif teklif para birimi", ["notes"] = "Notlar",
        }, "name")
        {
            References = new Dictionary<string, string> { ["opportunityId"] = Entities.Opportunity },
        },
        new(Entities.Activity, "Aktivite (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["type"] = "Tür", ["subject"] = "Konu", ["dueAt"] = "Son tarih", ["completedAt"] = "Tamamlanma", ["companyId"] = "Firma",
            ["contactId"] = "Kişi", ["opportunityId"] = "Fırsat", ["leadId"] = "Lead", ["outcome"] = "Sonuç", ["ownerId"] = "Sorumlu", ["notes"] = "Açıklama",
            ["status"] = "Durum", ["statusReason"] = "Durum / sonuç açıklaması", ["priority"] = "Öncelik", ["startAt"] = "Başlangıç", ["endAt"] = "Bitiş",
            ["isAllDay"] = "Tüm gün", ["startDate"] = "Başlangıç günü", ["endDate"] = "Bitiş günü", ["timeZone"] = "Saat dilimi", ["location"] = "Konum",
            ["onlineMeetingUrl"] = "Çevrim içi toplantı", ["isPrivate"] = "Özel", ["estimatedMinutes"] = "Tahmini süre (dk)", ["actualMinutes"] = "Gerçekleşen süre (dk)",
            ["rescheduleCount"] = "Ertelenme sayısı", ["previousActivityId"] = "Önceki aktivite", ["source"] = "Kaynak", ["createdBy"] = "Oluşturan",
            ["externalSource"] = "Dış kaynak", ["externalId"] = "Dış kimlik",
        }, "subject")
        {
            UserFields = ["ownerId", "createdBy"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["opportunityId"] = Entities.Opportunity, ["leadId"] = Entities.Lead,
                ["previousActivityId"] = Entities.Activity,
            },
        },
        new(Entities.ActivityLink, "Aktivite ilişkisi (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["activityId"] = "Aktivite", ["entityKey"] = "Kayıt türü", ["recordId"] = "Kayıt", ["isPrimary"] = "Birincil",
        })
        {
            TitleFields = ["entityKey"],
            References = new Dictionary<string, string> { ["activityId"] = Entities.Activity },
        },
        new(Entities.ActivityParticipant, "Aktivite katılımcısı (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["activityId"] = "Aktivite", ["userId"] = "Kullanıcı", ["contactId"] = "Kişi", ["email"] = "E-posta", ["displayName"] = "Ad", ["role"] = "Rol",
        })
        {
            TitleFields = ["displayName", "email"],
            UserFields = ["userId"],
            References = new Dictionary<string, string> { ["activityId"] = Entities.Activity, ["contactId"] = Entities.Contact },
        },
        new(Entities.ProductCategory, "Ürün kategorisi (CRM)", Permissions.ProductsRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["parentId"] = "Üst kategori", ["isActive"] = "Aktif",
        }, "name")
        {
            References = new Dictionary<string, string> { ["parentId"] = Entities.ProductCategory },
        },
        new(Entities.MeasureUnit, "Ölçü birimi (CRM)", Permissions.ProductsRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["groupKey"] = "Birim grubu", ["factorToBase"] = "Temel birime katsayı",
        }, "name"),
        new(Entities.Product, "Ürün / hizmet (CRM)", Permissions.ProductsRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama", ["kind"] = "Tür", ["status"] = "Durum", ["categoryId"] = "Kategori",
            ["baseUnitId"] = "Temel birim", ["externalCode"] = "Dış sistem kodu", ["barcode"] = "Barkod", ["brand"] = "Marka", ["model"] = "Model",
            ["tags"] = "Etiketler", ["saleStart"] = "Satış başlangıcı", ["saleEnd"] = "Satış bitişi", ["taxCategory"] = "Vergi kategorisi",
            ["priceIncludesTax"] = "Fiyat vergi dahil", ["quantityPrecision"] = "Miktar hassasiyeti", ["cost"] = "Maliyet", ["costCurrency"] = "Maliyet para birimi",
            ["serviceDurationMinutes"] = "Hizmet süresi (dk)", ["deliveryMode"] = "Teslim şekli", ["chargeType"] = "Ücretlendirme", ["recurringPeriod"] = "Dönem",
        }, "name")
        {
            References = new Dictionary<string, string> { ["categoryId"] = Entities.ProductCategory, ["baseUnitId"] = Entities.MeasureUnit },
        },
        new(Entities.TaxRate, "Vergi oranı (CRM)", Permissions.PricesRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["ratePercent"] = "Oran (%)", ["validFrom"] = "Geçerlilik başı", ["validTo"] = "Geçerlilik sonu", ["isActive"] = "Aktif",
        }, "name"),
        new(Entities.ExchangeRate, "Döviz kuru (CRM)", Permissions.PricesRead, new Dictionary<string, string>
        {
            ["fromCurrency"] = "Kaynak para birimi", ["toCurrency"] = "Hedef para birimi", ["rate"] = "Kur", ["validOn"] = "Geçerli gün", ["source"] = "Kaynak", ["note"] = "Not",
        }, "fromCurrency"),
        new(Entities.PriceList, "Fiyat listesi (CRM)", Permissions.PricesRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["currency"] = "Para birimi", ["scope"] = "Kapsam", ["companyId"] = "Firma", ["segment"] = "Segment", ["validFrom"] = "Geçerlilik başı",
            ["validTo"] = "Geçerlilik sonu", ["status"] = "Durum", ["tierMethod"] = "Kademe yöntemi", ["publishedAt"] = "Yayım zamanı",
        }, "name")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company },
        },
        new(Entities.PriceListItem, "Fiyat listesi satırı (CRM)", Permissions.PricesRead, new Dictionary<string, string>
        {
            ["priceListId"] = "Fiyat listesi", ["productId"] = "Ürün", ["minQuantity"] = "Alt miktar", ["maxQuantity"] = "Üst miktar", ["unitPrice"] = "Birim fiyat", ["isFree"] = "Ücretsiz",
        }, "productId")
        {
            References = new Dictionary<string, string> { ["priceListId"] = Entities.PriceList, ["productId"] = Entities.Product },
        },
        new(Entities.ProductComponent, "Paket bileşeni (CRM)", Permissions.ProductsRead, new Dictionary<string, string>
        {
            ["packageId"] = "Paket", ["componentId"] = "Bileşen", ["quantity"] = "Miktar",
        }, "componentId")
        {
            References = new Dictionary<string, string> { ["packageId"] = Entities.Product, ["componentId"] = Entities.Product },
        },
        new(Entities.DiscountPolicy, "İndirim politikası (CRM)", Permissions.PricesRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["percent"] = "Yüzde", ["amount"] = "Tutar", ["currency"] = "Para birimi", ["priority"] = "Öncelik", ["stackable"] = "Birleşebilir",
            ["maxTotalPercent"] = "Azami toplam %", ["approvalAbovePercent"] = "Onay eşiği %", ["validFrom"] = "Geçerlilik başı", ["validTo"] = "Geçerlilik sonu", ["isActive"] = "Aktif",
        }, "name"),
        new(Entities.Quote, "Teklif (CRM)", Permissions.QuotesRead, new Dictionary<string, string>
        {
            ["quoteNumber"] = "Teklif no", ["revisionNo"] = "Revizyon", ["title"] = "Başlık", ["description"] = "Açıklama", ["companyId"] = "Firma",
            ["contactId"] = "Kişi", ["opportunityId"] = "Fırsat", ["priceListId"] = "Fiyat listesi", ["ownerId"] = "Sorumlu", ["status"] = "Durum",
            ["approvalStatus"] = "Onay durumu", ["deliveryStatus"] = "Gönderim durumu", ["statusReason"] = "Durum gerekçesi", ["issueDate"] = "Düzenleme tarihi",
            ["validUntil"] = "Geçerlilik bitişi", ["currency"] = "Para birimi", ["priceIncludesTax"] = "Fiyatlar vergi dahil", ["documentDiscountKind"] = "Genel indirim türü",
            ["documentDiscountValue"] = "Genel indirim", ["paymentTerms"] = "Ödeme koşulları", ["deliveryTerms"] = "Teslimat koşulları",
            ["commercialTerms"] = "Ticari koşullar", ["internalNotes"] = "İç notlar", ["netTotal"] = "Net toplam", ["taxTotal"] = "Vergi", ["grossTotal"] = "Genel toplam",
        }, "title")
        {
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["opportunityId"] = Entities.Opportunity, ["priceListId"] = Entities.PriceList,
            },
        },
        new(Entities.QuoteApprovalRule, "Teklif onay kuralı (CRM)", Permissions.QuoteRulesManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["priority"] = "Öncelik", ["isActive"] = "Etkin", ["currency"] = "Para birimi",
            ["minDiscountPercent"] = "İndirim eşiği %", ["minGrossTotal"] = "Genel toplam eşiği", ["approverId"] = "Onaylayan",
        }, "name"),
        new(Entities.QuoteLine, "Teklif kalemi (CRM)", Permissions.QuotesRead, new Dictionary<string, string>
        {
            ["quoteId"] = "Teklif", ["name"] = "Ad", ["kind"] = "Tür", ["productId"] = "Ürün", ["quantity"] = "Miktar", ["unitPrice"] = "Birim fiyat",
            ["discountKind"] = "İndirim türü", ["discountValue"] = "İndirim", ["taxRatePercent"] = "Vergi oranı (%)", ["role"] = "Rol", ["unitCost"] = "Maliyet",
        }, "name")
        {
            References = new Dictionary<string, string> { ["quoteId"] = Entities.Quote, ["productId"] = Entities.Product },
        },
        new(Entities.LeadForm, "Lead formu (CRM)", Permissions.LeadFormsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["formKey"] = "Form kimliği", ["isActive"] = "Aktif", ["defaultOwnerId"] = "Varsayılan sorumlu",
            ["allowedOrigins"] = "İzinli kaynaklar", ["serverOnly"] = "Yalnızca sunucudan", ["secretRotatedAt"] = "Sır yenilenme",
        }, "name")
        {
            UserFields = ["defaultOwnerId"],
        },
        new(Entities.Lead, "Lead (CRM)", Permissions.LeadsRead, new Dictionary<string, string>
        {
            ["leadNumber"] = "Lead no", ["subject"] = "Konu", ["description"] = "Açıklama", ["status"] = "Durum", ["statusReason"] = "Durum gerekçesi",
            ["priority"] = "Öncelik", ["firstName"] = "Ad", ["lastName"] = "Soyad", ["companyName"] = "Firma adı", ["email"] = "E-posta",
            ["phone"] = "Telefon", ["jobTitle"] = "Unvan", ["website"] = "Web sitesi", ["country"] = "Ülke", ["city"] = "Şehir",
            ["companyId"] = "Mevcut firma", ["contactId"] = "Mevcut kişi", ["productInterest"] = "İlgilenilen ürün/hizmet",
            ["needDescription"] = "İhtiyaç", ["estimatedBudget"] = "Tahmini bütçe", ["currency"] = "Para birimi",
            ["targetPurchaseDate"] = "Hedef satın alma tarihi", ["industry"] = "Sektör", ["source"] = "Kaynak", ["sourceDetail"] = "Kaynak ayrıntısı",
            ["referrer"] = "Yönlendiren", ["externalSource"] = "Dış kaynak", ["externalSubmissionId"] = "Dış gönderim no",
            ["utmSource"] = "UTM kaynak", ["utmMedium"] = "UTM ortam", ["utmCampaign"] = "UTM kampanya", ["firstReceivedAt"] = "İlk geliş",
            ["ownerId"] = "Sorumlu", ["assignedAt"] = "Atanma", ["firstContactAt"] = "İlk temas", ["followUpAt"] = "Takip tarihi",
            ["qualifiedAt"] = "Nitelendirilme", ["disqualifiedAt"] = "Elenme", ["disqualificationReason"] = "Eleme nedeni",
            ["convertedAt"] = "Dönüşüm", ["convertedBy"] = "Dönüştüren", ["convertedCompanyId"] = "Dönüşüm firması",
            ["convertedContactId"] = "Dönüşüm kişisi", ["convertedOpportunityId"] = "Dönüşüm fırsatı",
        }, "subject")
        {
            UserFields = ["ownerId", "convertedBy"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["convertedCompanyId"] = Entities.Company,
                ["convertedContactId"] = Entities.Contact, ["convertedOpportunityId"] = Entities.Opportunity,
            },
        },
    ];

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): firma ve kişi izinleri, firmalar ve kişiler tarafların ve ortak kişilerin uzantısı olduğu
    /// için ortak kaydı da açar. Firma silinince taraf kalır; taraf, CRM firması varken silinemez.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.CompaniesRead, Permissions.CompaniesWrite),
        new(SharedEntities.Person, Permissions.ContactsRead, Permissions.ContactsWrite),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Company, "partyId", "CRM firması"),
        new(SharedEntities.Person, Entities.Contact, "personId", "CRM kişisi"),
    ];

    /// <summary>
    /// Aktivite tamamlanınca Lead'in ilk temasını işler (yalnızca Lead aktiviteleri; sistem adına, audit'li). Abone idempotenttir:
    /// aynı olay tekrar gelse de ilk temas yalnızca daha erken bir temas bulunursa değişir.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        // Herkese açık Lead toplama ucunun sistem kimliği: olay işlemez, yalnızca "crm.lead-intake" kimliğinin izinlerini tanımlar
        // (web formu isteği oturumsuzdur; Lead'i bu dar izinlerle, sistem adına ve audit'te bu anahtarla oluşturur).
        EventSubscription.For<LeadIntakeAnchor>(
            "crm.lead-intake",
            [DomainEventTypes.RecordDeleted],
            [Entities.LeadForm],
            [Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadFormsManage]),
        // Herkese açık müşteri teklif sayfasının sistem kimliği (ADR-0019): yalnızca belirteçle bağlı olduğu teklife karar yazar.
        EventSubscription.For<QuoteCustomerAnchor>(
            "crm.quote-customer",
            [DomainEventTypes.RecordDeleted],
            [Entities.Quote],
            [Permissions.QuotesRead, Permissions.QuotesRecordDecision]),
        // Teklif yaşam döngüsü yan etkileri (ADR-0019 dilim 7): kabulde fırsat kazanma, sunumda takip aktivitesi (ikisi de ayarlı).
        EventSubscription.For<QuoteLifecycleSubscriber>(
            "crm.quote-lifecycle",
            [DomainEventTypes.RecordUpdated],
            [Entities.Quote],
            [Permissions.QuotesRead, Permissions.CompaniesRead, Permissions.OpportunitiesRead, Permissions.OpportunityAmountsRead, Permissions.OpportunitiesWrite, Permissions.OpportunitiesClose, Permissions.ActivitiesRead, Permissions.ActivitiesWrite]),
        // Zamanlanmış süre dolumu işinin sistem kimliği: olay işlemez, yalnızca izinlerini tanımlar.
        EventSubscription.For<QuoteExpiryAnchor>(
            "crm.quote-expiry",
            [DomainEventTypes.RecordDeleted],
            [Entities.Quote],
            [Permissions.QuotesRead, Permissions.QuotesRecordDecision, Permissions.CompaniesRead, Permissions.OpportunitiesRead, Permissions.ActivitiesRead, Permissions.ActivitiesWrite]),
        EventSubscription.For<LeadFirstContactSubscriber>(
            "crm.lead-first-contact",
            [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated],
            [Entities.Activity],
            [Permissions.ActivitiesRead, Permissions.ActivitiesViewPrivate, Permissions.LeadsRead, Permissions.LeadsUpdate]),
        // Tamamlanan fırsat aktivitesinden son müşteri etkileşimini işler; yalnızca bu tek alanı yazar.
        EventSubscription.For<OpportunityInteractionSubscriber>(
            "crm.opportunity-interaction",
            [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated],
            [Entities.Activity],
            [Permissions.ActivitiesRead, Permissions.ActivitiesViewPrivate, Permissions.OpportunitiesRead, Permissions.OpportunitiesWrite]),
    ];

    /// <summary>Tenant ayar anahtarları.</summary>
    public static class SettingKeys
    {
        public const string LeadFirstContactTargetHours = "crm.lead.first-contact-target-hours";
        public const string OpportunityStageRequirements = "crm.opportunity.stage-requirements";
        public const string OpportunityStageProbabilities = "crm.opportunity.stage-probabilities";
        public const string OpportunityStaleDays = "crm.opportunity.stale-days";
        public const string QuoteApprovalDiscountPercent = "crm.quote.approval-discount-percent";
        public const string QuoteApprovalGrossAbove = "crm.quote.approval-gross-above";
        public const string QuoteDefaultValidityDays = "crm.quote.default-validity-days";
        public const string QuoteAllowSelfApproval = "crm.quote.allow-self-approval";
        public const string QuoteSellerDetails = "crm.quote.seller-details";
        public const string QuoteFooterNote = "crm.quote.footer-note";
        public const string QuoteOnAcceptWinOpportunity = "crm.quote.on-accept-win-opportunity";
        public const string QuoteFollowUpDays = "crm.quote.follow-up-days";
        public const string QuoteExpiryReminderDays = "crm.quote.expiry-reminder-days";
        public const string QuoteSyncOpportunityAmount = "crm.quote.sync-opportunity-amount";
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<CrmStoreContext>();
        services.AddScoped<ISharedRecordVisibility, CrmSharedRecordVisibility>();
        services.AddScoped<IRecordStore, CompanyRecordStore>();
        services.AddScoped<IRecordStore, ContactRecordStore>();
        services.AddScoped<IRecordStore, CompanyContactRecordStore>();
        services.AddScoped<IRecordStore, LocationRecordStore>();
        services.AddScoped<IRecordStore, AddressRecordStore>();
        services.AddScoped<IRecordStore, CompanyContactPointRecordStore>();
        services.AddScoped<IRecordStore, PersonContactPointRecordStore>();
        services.AddScoped<IRecordStore, CompanyRelationRecordStore>();
        services.AddScoped<IRecordStore, OpportunityRecordStore>();
        services.AddScoped<IRecordStore, ProductCategoryRecordStore>();
        services.AddScoped<IRecordStore, MeasureUnitRecordStore>();
        services.AddScoped<IRecordStore, ProductRecordStore>();
        services.AddScoped<IRecordStore, TaxRateRecordStore>();
        services.AddScoped<IRecordStore, ExchangeRateRecordStore>();
        services.AddScoped<IRecordStore, PriceListRecordStore>();
        services.AddScoped<IRecordStore, PriceListItemRecordStore>();
        services.AddScoped<IRecordStore, ProductComponentRecordStore>();
        services.AddScoped<IRecordStore, DiscountPolicyRecordStore>();
        services.AddScoped<IRecordStore, QuoteRecordStore>();
        services.AddScoped<IRecordStore, QuoteLineRecordStore>();
        services.AddScoped<IRecordStore, QuoteApprovalRuleRecordStore>();
        services.AddScoped<QuoteWorkflowGate>();
        services.AddScoped<QuoteReader>();
        services.AddScoped<IQuoteWorkflow, QuoteWorkflow>();
        services.AddScoped<IQuoteSharing, QuoteSharing>();
        services.AddScoped<IQuoteComparison, QuoteComparisonService>();
        services.AddScoped<IQuoteReport, QuoteReport>();
        services.AddScoped<QuoteMarkers>();
        services.AddScoped<IQuoteExpiry, QuoteExpiry>();
        services.AddScoped<IAcceptedQuoteSource, AcceptedQuoteSource>();
        services.AddScoped<IQuoteOpportunitySync, QuoteOpportunitySync>();
        services.AddScoped<QuoteLifecycleSubscriber>();
        services.AddScoped<QuoteExpiryAnchor>();
        services.AddScoped<IQuoteCustomerPortal, QuoteCustomerPortal>();
        services.AddScoped<QuoteCustomerAnchor>();
        services.AddScoped<IPriceCalculator, PriceCalculator>();
        services.AddScoped<IRecordStore, OpportunityContactRecordStore>();
        services.AddScoped<IRecordStore, OpportunityCompetitorRecordStore>();
        services.AddScoped<OpportunityWorkflowGate>();
        services.AddScoped<OpportunityHistoryReader>();
        services.AddScoped<IOpportunityWorkflow, OpportunityWorkflow>();
        services.AddScoped<IOpportunityReport, OpportunityReport>();
        services.AddScoped<OpportunityInteractionSubscriber>();
        services.AddScoped<IRecordStore, ActivityRecordStore>();
        services.AddScoped<IRecordStore, ActivityLinkRecordStore>();
        services.AddScoped<IRecordStore, ActivityParticipantRecordStore>();
        services.AddScoped<IActivityLinkTarget, CompanyLinkTarget>();
        services.AddScoped<IActivityLinkTarget, ContactLinkTarget>();
        services.AddScoped<IActivityLinkTarget, OpportunityLinkTarget>();
        services.AddScoped<IActivityLinkTarget, LeadLinkTarget>();
        services.AddScoped<IActivityLinkTarget, QuoteLinkTarget>();
        services.AddScoped<ActivityWorkflowGate>();
        services.AddScoped<IActivityWorkflow, ActivityWorkflow>();
        services.AddScoped<IRecordStore, LeadRecordStore>();
        services.AddScoped<IRecordStore, LeadFormRecordStore>();
        services.AddScoped<ILeadIntake, LeadIntake>();
        services.AddScoped<ILeadFormAdmin, LeadFormAdmin>();
        services.AddScoped<LeadIntakeAnchor>();
        services.AddScoped<LeadWorkflowGate>();
        services.AddScoped<ILeadQualificationRule, MinimumQualificationRule>();
        services.AddScoped<ILeadWorkflow, LeadWorkflow>();
        services.AddScoped<ILeadConversion, LeadConversion>();
        services.AddScoped<LeadFirstContactSubscriber>();
    }
}
