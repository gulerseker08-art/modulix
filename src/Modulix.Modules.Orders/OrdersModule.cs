using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Orders.Lifecycle;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders;

/// <summary>
/// Sipariş ve satış devri (EPIC 8, ADR-0023): kabul edilmiş tekliften sipariş, satış devri, operasyon işi ve teslimat/hizmet başlangıcı.
/// Sektörden bağımsızdır; müşteri ve kişiler ortak kayıttır (ADR-0006), CRM tablolarına bağlanmaz. Teklifi yalnızca
/// <c>Modulix.Core.Contracts</c> üzerindeki <c>IAcceptedQuoteSource</c> ile okur. Sipariş tutarı tahsilat ya da gelir değildir.
/// </summary>
public sealed class OrdersModule : IModule
{
    public const string ModuleKey = "orders";

    public static class Permissions
    {
        public const string OrdersRead = "orders.orders.read";

        /// <summary>Kabul edilmiş tekliften sipariş oluşturur. Düzenlemeden ayrıdır.</summary>
        public const string OrdersCreate = "orders.orders.create";

        public const string OrdersWrite = "orders.orders.write";

        /// <summary>Sipariş tutarlarını, kalem fiyatlarını ve toplamları görür. Olmayan kişi siparişi görür, tutarları görmez ve tekliften sipariş oluşturamaz.</summary>
        public const string FinancialsRead = "orders.financials.read";

        /// <summary>Onay bekleyen siparişi onaylar ya da gerekçeyle taslağa geri gönderir; onaylanmış siparişin değişiklik talebine karar verir. Yazma izninden ayrıdır.</summary>
        public const string OrdersApprove = "orders.orders.approve";

        /// <summary>Siparişi iptal eder (teslim edilen miktar ve kayıtları korunur). Ayrı ve kritik bir yetkidir.</summary>
        public const string OrdersCancel = "orders.orders.cancel";

        /// <summary>Gerçekleşmesi tamamlanmış siparişi kapatır.</summary>
        public const string OrdersClose = "orders.orders.close";

        /// <summary>Kalan yükümlülük varken siparişi gerekçeyle zorla kapatır. Kapatma yetkisinden ayrıdır.</summary>
        public const string OrdersForceClose = "orders.orders.force-close";

        /// <summary>Satış devri kaydını hazırlar ve düzenler (kontrol listesi dahil). Göndermekten ve kabul etmekten ayrıdır.</summary>
        public const string HandoversWrite = "orders.handovers.write";

        /// <summary>Hazır satış devrini operasyona gönderir ya da bilgi isteği sonrası yeniden gönderir.</summary>
        public const string HandoversSend = "orders.handovers.send";

        /// <summary>Operasyon adına devri kabul eder ya da gerekçeyle bilgi ister; sorumlu operasyon kullanıcısını atar.</summary>
        public const string HandoversAccept = "orders.handovers.accept";

        /// <summary>Operasyon işine sorumlu kullanıcı ve ekip atar. İşi yönetmekten ayrıdır.</summary>
        public const string JobsAssign = "orders.jobs.assign";

        /// <summary>Operasyon işini elle açar, başlatır, tamamlar, iptal eder; görev ve bağımlılıkları yönetir.</summary>
        public const string JobsManage = "orders.jobs.manage";
    }

    /// <summary>Firma ayarları.</summary>
    public static class SettingKeys
    {
        public const string AllowSelfApproval = "orders.approval.allow-self";
        public const string HandoverAcceptDueDays = "orders.handover.accept-due-days";
        public const string HandoverWaitHours = "orders.handover.wait-hours";
        public const string JobStrategy = "orders.jobs.strategy";
        public const string JobServiceAfterDelivery = "orders.jobs.service-after-delivery";
        public const string JobUnassignedHours = "orders.jobs.unassigned-hours";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Order = "orders.order";
        public const string OrderLine = "orders.order-line";
        public const string ChangeRequest = "orders.change-request";
        public const string Handover = "orders.handover";
        public const string HandoverItem = "orders.handover-item";
        public const string Job = "orders.operation-job";
        public const string JobTask = "orders.operation-task";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Siparişler";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.OrdersRead, "Siparişleri, kalemlerini ve tarihçesini görüntüler", "Siparişler"),
        new(Permissions.OrdersCreate, "Kabul edilmiş tekliften sipariş oluşturur", "Siparişler"),
        new(Permissions.OrdersWrite, "Taslak siparişin ticari alanlarını ve planlama bilgilerini düzenler", "Siparişler"),
        new(Permissions.FinancialsRead, "Sipariş tutarlarını, kalem fiyatlarını ve toplamları görür", "Sipariş tutarları"),
        new(Permissions.OrdersApprove, "Onay bekleyen siparişi onaylar ya da gerekçeyle geri gönderir; değişiklik talebine karar verir", "Siparişler"),
        new(Permissions.OrdersCancel, "Siparişi gerekçeyle iptal eder (teslim edilen kayıtlar korunur)", "Siparişler"),
        new(Permissions.OrdersClose, "Gerçekleşmesi tamamlanmış siparişi kapatır", "Siparişler"),
        new(Permissions.OrdersForceClose, "Kalan yükümlülük varken siparişi gerekçeyle zorla kapatır", "Siparişler"),
        new(Permissions.HandoversWrite, "Satış devri kaydını ve kontrol listesini hazırlar ve düzenler", "Satış devri"),
        new(Permissions.HandoversSend, "Hazır satış devrini operasyona gönderir ya da yeniden gönderir", "Satış devri"),
        new(Permissions.HandoversAccept, "Satış devrini operasyon adına kabul eder ya da gerekçeyle bilgi ister", "Satış devri"),
        new(Permissions.JobsAssign, "Operasyon işine sorumlu kullanıcı ve ekip atar", "Operasyon işleri"),
        new(Permissions.JobsManage, "Operasyon işini elle açar, başlatır, tamamlar, iptal eder; görev ve bağımlılıkları yönetir", "Operasyon işleri"),
    ];

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.JobStrategy, "Operasyon işi: oluşturma stratejisi", "Siparişler", SettingType.Choice, "PerKind")
        {
            Description = "Devir kabul edilince işlerin nasıl açılacağı. Türe göre: ürün kalemleri tek 'Teslimat', hizmet/paket kalemleri tek 'Hizmet başlangıcı' işi olur. Tek iş: bütün kalemler tek işte. Kalem başına: her kalem ayrı iş.",
            Options = [new SettingOption("PerKind", "Türe göre (teslimat ve hizmet ayrı)"), new SettingOption("Single", "Tek iş"), new SettingOption("PerLine", "Kalem başına")],
        },
        new(SettingKeys.JobServiceAfterDelivery, "Operasyon işi: hizmet işi teslimat işinden sonra başlasın", "Siparişler", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse (türe göre stratejide) hizmet başlangıcı işi teslimat işi tamamlanmadan başlatılamaz; bağımlılık sunucuda uygulanır.",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.JobUnassignedHours, "Operasyon işi: atama bekleme uyarı süresi (saat)", "Siparişler", SettingType.Text, "24")
        {
            Description = "Atama bekleyen iş bu süreden uzun atanmazsa devri kabul eden kullanıcıya bir kez takip aktivitesi açılır. 0 kapalıdır.",
            Pattern = @"^[0-9]{1,4}$",
            PatternMessage = "0-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        new(SettingKeys.HandoverAcceptDueDays, "Satış devri: kabul için hedef süre (gün)", "Siparişler", SettingType.Text, "2")
        {
            Description = "Operasyona gönderilen devir için açılan takip aktivitesinin son tarihi (gönderimden itibaren gün). 1-30 arası.",
            Pattern = @"^[0-9]{1,2}$",
            PatternMessage = "1-30 arası tam sayı girin.",
            MaxLength = 2,
        },
        new(SettingKeys.HandoverWaitHours, "Satış devri: kabul bekleme uyarı süresi (saat)", "Siparişler", SettingType.Text, "48")
        {
            Description = "Operasyona gönderilen devir bu süreden uzun kabul edilmezse satış sorumlusuna bir kez takip aktivitesi açılır. 0 kapalıdır.",
            Pattern = @"^[0-9]{1,4}$",
            PatternMessage = "0-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        new(SettingKeys.AllowSelfApproval, "Sipariş onayı: kendi gönderdiğini onaylama", "Siparişler", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse siparişi onaya gönderen kişi onaylama yetkisine sahipse kendi gönderdiğini onaylayabilir (aynı kural değişiklik talebi için de geçerlidir). Varsayılan hayırdır (hazırlayan ve onaylayan ayrılır).",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
    ];

    /// <summary>Devre dışı bırakılan kişinin siparişleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Order, "sipariş") { Path = "siparisler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "orders.sales",
            "Sipariş sorumlusu (satış)",
            "Kabul edilmiş tekliften sipariş oluşturur, siparişleri tutarlarıyla görüntüler ve taslağı düzenler.",
            [Permissions.OrdersRead, Permissions.OrdersCreate, Permissions.OrdersWrite, Permissions.FinancialsRead, Permissions.HandoversWrite, Permissions.HandoversSend])
        {
            Scope = RecordScope.Own,
        },
        new(
            "orders.operations",
            "Operasyon sorumlusu (sipariş)",
            "Siparişleri tutarlar olmadan görüntüler; satış devirlerini kabul eder ya da gerekçeyle bilgi ister; operasyon işlerini atar ve yürütür.",
            [Permissions.OrdersRead, Permissions.HandoversAccept, Permissions.JobsAssign, Permissions.JobsManage])
        {
            Scope = RecordScope.All,
        },
        new(
            "orders.approver",
            "Sipariş onaylayıcı",
            "Siparişleri tutarlarıyla görüntüler; onaylar, geri gönderir, iptal eder ve değişiklik taleplerine karar verir.",
            [Permissions.OrdersRead, Permissions.FinancialsRead, Permissions.OrdersApprove, Permissions.OrdersCancel])
        {
            Scope = RecordScope.All,
        },
        new(
            "orders.closer",
            "Sipariş kapatma sorumlusu",
            "Tamamlanan siparişleri kapatır; kalan yükümlülük varken zorla kapatma yetkisi içermez.",
            [Permissions.OrdersRead, Permissions.OrdersClose])
        {
            Scope = RecordScope.All,
        },
        new(
            "orders.viewer",
            "Sipariş izleyici",
            "Siparişleri tutarlar olmadan görüntüler.",
            [Permissions.OrdersRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Order, "Sipariş", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["number"] = "Numara", ["title"] = "Başlık", ["partyId"] = "Müşteri", ["personId"] = "İlgili kişi", ["ownerId"] = "Satış sorumlusu",
            ["sourceQuoteNumber"] = "Kaynak teklif", ["sourceQuoteRevisionNo"] = "Teklif revizyonu", ["contractNumber"] = "Sözleşme", ["conversionMode"] = "Dönüşüm kipi",
            ["acceptedAt"] = "Müşteri kabul zamanı", ["acceptanceMethod"] = "Kabul yöntemi", ["acceptedByName"] = "Kabul eden", ["status"] = "Durum",
            ["handoverStatus"] = "Devir durumu", ["fulfillmentStatus"] = "Gerçekleşme", ["statusReason"] = "Gerekçe", ["customerName"] = "Müşteri ünvanı",
            ["billingAddress"] = "Fatura adresi", ["deliveryAddress"] = "Teslimat adresi", ["currency"] = "Para birimi", ["priceIncludesTax"] = "Fiyatlar vergi dahil",
            ["paymentTerms"] = "Ödeme koşulları", ["deliveryTerms"] = "Teslimat koşulları", ["commercialTerms"] = "Ticari koşullar", ["commitments"] = "Taahhütler",
            ["operationsNotes"] = "Operasyon notları", ["location"] = "Lokasyon", ["plannedStartDate"] = "Planlanan başlangıç", ["plannedDeliveryDate"] = "Planlanan teslim",
            ["netTotal"] = "Net toplam", ["taxTotal"] = "Vergi", ["grossTotal"] = "Genel toplam",
        }, "title")
        {
            UserFields = ["ownerId", "acceptedByUserId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person },
        },
        new(Entities.ChangeRequest, "Sipariş değişiklik talebi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["orderId"] = "Sipariş", ["sequence"] = "Sıra", ["status"] = "Durum", ["reason"] = "Gerekçe", ["changes"] = "Önerilen değişiklikler", ["impact"] = "Etki özeti",
            ["netDelta"] = "Net tutar farkı", ["taxDelta"] = "Vergi farkı", ["decidedAt"] = "Karar zamanı", ["decisionNote"] = "Karar notu",
        })
        {
            UserFields = ["requestedBy", "decidedBy"],
            References = new Dictionary<string, string> { ["orderId"] = Entities.Order },
        },
        new(Entities.Handover, "Satış devri", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["orderId"] = "Sipariş", ["status"] = "Durum", ["round"] = "Gönderim turu", ["scope"] = "Satılan kapsam", ["outOfScope"] = "Kapsam dışı işler",
            ["deliverables"] = "Teslim edilecek çıktılar", ["commitments"] = "Müşteri taahhütleri", ["location"] = "Lokasyon", ["contactName"] = "İlgili kişi",
            ["contactPhone"] = "Telefon", ["contactEmail"] = "E-posta", ["targetStartDate"] = "Hedef başlangıç", ["targetEndDate"] = "Hedef bitiş", ["priority"] = "Öncelik",
            ["requiredDocuments"] = "Gerekli belgeler", ["customerPrerequisites"] = "Müşteri ön koşulları", ["paymentConditions"] = "Ödeme/sözleşme koşulları",
            ["risks"] = "Riskler", ["specialRequests"] = "Özel talepler", ["dependencies"] = "Bağımlılıklar", ["responsibleUserId"] = "Sorumlu operasyon kullanıcısı",
            ["responsibleTeam"] = "Sorumlu operasyon ekibi", ["completionCriteria"] = "Tamamlanma kriterleri", ["acceptanceCriteria"] = "Müşteri kabul kriterleri",
            ["infoRequest"] = "Bilgi isteği", ["sentAt"] = "Gönderim zamanı", ["acceptedAt"] = "Kabul zamanı",
        })
        {
            UserFields = ["responsibleUserId", "sentBy", "acceptedBy", "createdBy"],
            References = new Dictionary<string, string> { ["orderId"] = Entities.Order },
        },
        new(Entities.HandoverItem, "Devir kontrol listesi maddesi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["handoverId"] = "Devir", ["position"] = "Sıra", ["text"] = "Madde", ["isRequired"] = "Zorunlu", ["isDone"] = "Tamamlandı", ["doneAt"] = "Tamamlanma zamanı", ["note"] = "Not",
        })
        {
            UserFields = ["doneBy"],
            References = new Dictionary<string, string> { ["handoverId"] = Entities.Handover },
        },
        new(Entities.Job, "Operasyon işi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["orderId"] = "Sipariş", ["number"] = "Numara", ["title"] = "Başlık", ["kind"] = "Tür", ["groupKey"] = "Üretim grubu", ["status"] = "Durum", ["priority"] = "Öncelik",
            ["responsibleUserId"] = "Sorumlu", ["teamName"] = "Ekip", ["plannedStartDate"] = "Planlanan başlangıç", ["plannedEndDate"] = "Planlanan bitiş",
            ["actualStartAt"] = "Gerçekleşen başlangıç", ["actualEndAt"] = "Gerçekleşen bitiş", ["location"] = "Lokasyon", ["notes"] = "Not", ["cancelReason"] = "İptal gerekçesi",
            ["source"] = "Kaynak",
        }, "title")
        {
            UserFields = ["responsibleUserId", "createdBy"],
            References = new Dictionary<string, string> { ["orderId"] = Entities.Order },
        },
        new(Entities.JobTask, "Operasyon işi görevi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["jobId"] = "İş", ["position"] = "Sıra", ["title"] = "Görev", ["isRequired"] = "Zorunlu", ["isDone"] = "Tamamlandı", ["doneAt"] = "Tamamlanma zamanı", ["note"] = "Not",
        }, "title")
        {
            UserFields = ["doneBy"],
            References = new Dictionary<string, string> { ["jobId"] = Entities.Job },
        },
        new(Entities.OrderLine, "Sipariş kalemi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["orderId"] = "Sipariş", ["position"] = "Sıra", ["kind"] = "Tür", ["productCode"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama",
            ["quantity"] = "Miktar", ["acceptedQuantity"] = "Kabul edilen miktar", ["unitName"] = "Birim", ["unitPrice"] = "Birim fiyat",
            ["taxRatePercent"] = "Vergi %", ["net"] = "Net", ["tax"] = "Vergi", ["gross"] = "Toplam", ["deliveredQuantity"] = "Teslim edilen", ["cancelledQuantity"] = "İptal edilen miktar",
            ["plannedDate"] = "Planlanan tarih",
        }, "name")
        {
            References = new Dictionary<string, string> { ["orderId"] = Entities.Order },
        },
    ];

    /// <summary>Hassas alanlar: tutarlar. Para birimi tek başına bir şey söylemediği için hassas değildir.</summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Order, Permissions.FinancialsRead, ["netTotal", "taxTotal", "grossTotal"]),
        new(Entities.ChangeRequest, Permissions.FinancialsRead, ["netDelta", "taxDelta"]),
        new(Entities.OrderLine, Permissions.FinancialsRead, ["unitPrice", "listAmount", "lineDiscount", "net", "tax", "gross"]),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Order, "Sipariş"),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => OrdersLookupDefinitions.All;

    /// <summary>Ortak kayıtlar (ADR-0006): müşteri ve kişi, sipariş okuma izniyle okunur. Sipariş modülü ortak kayıt oluşturmaz.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.OrdersRead),
        new(SharedEntities.Person, Permissions.OrdersRead),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Order, "partyId", "sipariş"),
        new(SharedEntities.Person, Entities.Order, "personId", "sipariş"),
    ];

    /// <summary>
    /// Sistem kimlikleri (oturumsuz çağıranlar): zamanlanmış işler (devir kabul bekleme takibi) bu kimlikle, kendi tenant kapsamında ve yalnızca bildirilen
    /// izinlerle çalışır. Olay işlemez; izinlerini tanımlar.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        EventSubscription.For<OrdersSchedulerAnchor>(
            "orders.scheduler",
            [DomainEventTypes.RecordDeleted],
            [Entities.Order],
            [Permissions.OrdersRead, Permissions.HandoversWrite, Permissions.JobsManage]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<OrdersStoreContext>();
        services.AddScoped<OrderWorkflowGate>();
        services.AddScoped<OrderReader>();
        services.AddScoped<IRecordStore, OrderRecordStore>();
        services.AddScoped<IRecordStore, OrderLineRecordStore>();
        services.AddScoped<IRecordStore, OrderChangeRequestRecordStore>();
        services.AddScoped<IRecordStore, OrderHandoverRecordStore>();
        services.AddScoped<IRecordStore, OrderHandoverItemRecordStore>();
        services.AddScoped<IOrderHandovers, OrderHandovers>();
        services.AddScoped<IRecordStore, OperationJobRecordStore>();
        services.AddScoped<IRecordStore, OperationJobTaskRecordStore>();
        services.AddScoped<OperationJobs>();
        services.AddScoped<IOperationJobs>(sp => sp.GetRequiredService<OperationJobs>());
        services.AddScoped<IOperationJobGenerator>(sp => sp.GetRequiredService<OperationJobs>());
        services.AddScoped<IOrderScheduler, OrderScheduler>();
        services.AddScoped<OrdersSchedulerAnchor>();
        services.AddScoped<IOrderWorkflow, OrderWorkflow>();
        services.AddScoped<IOrderChangeRequests, OrderChangeRequests>();
        services.AddScoped<IOrderFromQuote, OrderFromQuote>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IActivityLinkTarget, OrderLinkTarget>();
    }
}
