using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts;

/// <summary>
/// Sözleşme yönetimi (EPIC 7, ADR-0022): şablon, sürüm, onay, imza, taahhüt ve yenileme. Sektörden bağımsızdır; müşteri ve kişiler
/// ortak kayıttır (ADR-0006), CRM tablolarına bağlanmaz. Yalnızca <c>Modulix.Core.Contracts</c>'a başvurur.
/// </summary>
public sealed class ContractsModule : IModule
{
    public const string ModuleKey = "contracts";

    public static class Permissions
    {
        public const string ContractsRead = "contracts.contracts.read";
        public const string ContractsWrite = "contracts.contracts.write";

        /// <summary>Sözleşme bedeli, kalem fiyatları ve toplamlar (Y8b, hassas alan). Olmayan kişi sözleşmeyi görür, tutarları görmez ve yazamaz.</summary>
        public const string FinancialsRead = "contracts.financials.read";

        /// <summary>İç onay bekleyen sözleşmeyi onaylar ya da reddeder (ADR-0022, dilim 2). Yazma izninden ayrıdır.</summary>
        public const string ContractsApprove = "contracts.contracts.approve";

        /// <summary>Şablon metnini oluşturur ve düzenler (taslak çalışma kopyası). Yayımlamadan ayrıdır.</summary>
        public const string TemplatesEdit = "contracts.templates.edit";

        /// <summary>Şablonu yayımlar, arşivler ve geri alır; yayımlanan sürümden belge üretilir. Düzenlemeden ayrıdır.</summary>
        public const string TemplatesPublish = "contracts.templates.publish";

        /// <summary>Yayımlanmış şablondan sözleşme belgesi (PDF) üretir.</summary>
        public const string DocumentsGenerate = "contracts.documents.generate";

        /// <summary>Üretilmiş sözleşme belgesini indirir.</summary>
        public const string DocumentsDownload = "contracts.documents.download";

        /// <summary>Onay kurallarını ve onay vekâletlerini tanımlar (yönetim işlemi; denetim kaydına yazılır). Onaylamaktan ayrıdır.</summary>
        public const string ApprovalsManage = "contracts.approvals.manage";

        /// <summary>İmza isteği başlatır, iptal eder, hatırlatma ve ret kaydeder, simülasyon olayı tetikler. Doğrulamadan ayrıdır.</summary>
        public const string SignaturesManage = "contracts.signatures.manage";

        /// <summary>Manuel imzayı (imzalı belge taraması) doğrulayıp kaydeder; yöneten kişiden ayrı bir yetkidir.</summary>
        public const string SignaturesVerify = "contracts.signatures.verify";

        /// <summary>Sağlayıcı olaylarını uygular. Yalnızca sistem kimliğine (webhook) verilir; kullanıcı rollerine atanmamalıdır.</summary>
        public const string SignaturesProviderEvents = "contracts.signatures.provider-events";

        /// <summary>Taahhüt tanımlar, düzenler, iptal eder ve değerlendirme turunu (ihlal, eskalasyon) çalıştırır.</summary>
        public const string CommitmentsWrite = "contracts.commitments.write";

        /// <summary>Taahhüt gerçekleşmesini (dönem tamamlama, ölçüm, kanıt) kaydeder. Tanımlamadan ayrıdır.</summary>
        public const string CommitmentsComplete = "contracts.commitments.complete";

        /// <summary>Gerçekleşmeyi gerekçeyle muaf tutar (ihlal sayılmaz); ayrı yetkidir.</summary>
        public const string CommitmentsWaive = "contracts.commitments.waive";

        /// <summary>Yenileme taslağı açar, yenilememe kararı verir ve yenileme takvimini yönetir; zamanlayıcı sistem kimliği de bunu taşır.</summary>
        public const string RenewalsManage = "contracts.renewals.manage";

        /// <summary>Yürürlükteki sözleşmeyi fesheder (ya da fesih tarihini planlar). Ayrı ve kritik bir yetkidir.</summary>
        public const string ContractsTerminate = "contracts.contracts.terminate";
    }

    /// <summary>Firma ayarları (Y6).</summary>
    public static class SettingKeys
    {
        public const string AllowSelfApproval = "contracts.approval.allow-self";
        public const string ApprovalDueHours = "contracts.approval.due-hours";
        public const string SignatureProvider = "contracts.signature.provider";
        public const string SignatureValidityDays = "contracts.signature.validity-days";
        public const string RenewalReminderDays = "contracts.renewal.reminder-days";
        public const string AllowAutoRenewal = "contracts.renewal.allow-auto";
        public const string ProviderName = "contracts.document.provider-name";
        public const string ProviderDetails = "contracts.document.provider-details";
        public const string FooterNote = "contracts.document.footer-note";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Contract = "contracts.contract";
        public const string ContractParty = "contracts.contract-party";
        public const string ContractLine = "contracts.contract-line";
        public const string ContractTemplate = "contracts.contract-template";
        public const string ContractApprovalRule = "contracts.contract-approval-rule";
        public const string ContractApprovalDelegation = "contracts.contract-approval-delegation";
        public const string ContractCommitment = "contracts.contract-commitment";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string ContractType = "contracts.contract-type";
        public const string CommitmentCategory = "contracts.commitment-category";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Sözleşmeler";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ContractsRead, "Sözleşmeleri, taraflarını ve kalemlerini görüntüler", "Sözleşmeler"),
        new(Permissions.ContractsWrite, "Sözleşme taslağı açar, taraf ve kalem ekler, düzenler, taslağı siler", "Sözleşmeler"),
        new(Permissions.FinancialsRead, "Sözleşme bedelini, kalem fiyatlarını ve toplamları görür ve düzenler", "Sözleşme tutarları"),
        new(Permissions.ContractsApprove, "İç onay bekleyen sözleşmeyi onaylar ya da gerekçeyle reddeder", "Sözleşmeler"),
        new(Permissions.TemplatesEdit, "Sözleşme şablonu oluşturur ve taslak metnini düzenler", "Sözleşme şablonları"),
        new(Permissions.TemplatesPublish, "Sözleşme şablonunu yayımlar, arşivler ve geri alır", "Sözleşme şablonları"),
        new(Permissions.DocumentsGenerate, "Yayımlanmış şablondan sözleşme belgesi (PDF) üretir", "Sözleşme belgeleri"),
        new(Permissions.DocumentsDownload, "Üretilmiş sözleşme belgesini indirir", "Sözleşme belgeleri"),
        new(Permissions.ApprovalsManage, "Sözleşme onay kurallarını ve onay vekâletlerini tanımlar", "Sözleşme onayı"),
        new(Permissions.SignaturesManage, "Sözleşmeyi imzaya gönderir, imza isteğini iptal eder, hatırlatma ve ret kaydeder", "Sözleşme imzası"),
        new(Permissions.SignaturesVerify, "Manuel imzayı (imzalı belge) doğrulayıp kaydeder", "Sözleşme imzası"),
        new(Permissions.SignaturesProviderEvents, "İmza sağlayıcısının olaylarını uygular (yalnızca sistem kimliği; kullanıcıya atanmaz)", "Sözleşme imzası"),
        new(Permissions.CommitmentsWrite, "Sözleşme taahhüdü tanımlar, düzenler, iptal eder ve değerlendirme turunu çalıştırır", "Sözleşme taahhütleri"),
        new(Permissions.CommitmentsComplete, "Taahhüt gerçekleşmesini (dönem tamamlama, ölçüm, kanıt) kaydeder", "Sözleşme taahhütleri"),
        new(Permissions.CommitmentsWaive, "Taahhüt gerçekleşmesini gerekçeyle muaf tutar", "Sözleşme taahhütleri"),
        new(Permissions.RenewalsManage, "Sözleşme yenileme taslağı açar, yenilememe kararı verir, yenileme takvimini yönetir", "Sözleşme yenileme ve fesih"),
        new(Permissions.ContractsTerminate, "Yürürlükteki sözleşmeyi fesheder ya da fesih tarihini planlar", "Sözleşme yenileme ve fesih"),
    ];

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.AllowSelfApproval, "Sözleşme iç onay: kendi gönderdiğini onaylama", "Sözleşmeler", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse sözleşmeyi onaya gönderen kişi onaylama yetkisine sahipse kendi gönderdiğini onaylayabilir. Varsayılan hayırdır (hazırlayan ve onaylayan ayrılır).",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.ApprovalDueHours, "Sözleşme iç onay: hedef süre (saat)", "Sözleşmeler", SettingType.Text, "72")
        {
            Description = "Onaya gönderilen sözleşme için hedeflenen karar süresi. Süre aşılırsa onay gecikmiş görünür (onay iş listesinde ve sözleşmede). 0 kapalıdır.",
            Pattern = @"^[0-9]{1,4}$",
            PatternMessage = "0-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
        new(SettingKeys.SignatureProvider, "Sözleşme imzası: sağlayıcı", "Sözleşmeler", SettingType.Choice, "none")
        {
            Description = "Yok: yalnızca manuel imza (imzalı belge yüklenir, yetkili doğrular). Simülasyon: yalnızca test içindir; kimseye istek ya da e-posta göndermez ve imzaların hiçbir hukuki değeri yoktur.",
            Options = [new SettingOption("none", "Yok (yalnızca manuel imza)"), new SettingOption("simulation", "Simülasyon (test; hukuki değeri yoktur)")],
        },
        new(SettingKeys.SignatureValidityDays, "Sözleşme imzası: istek geçerlilik süresi (gün)", "Sözleşmeler", SettingType.Text, "14")
        {
            Description = "İmza isteği bu kadar gün içinde tamamlanmazsa süresi dolar. 1-90 arası.",
            Pattern = @"^[0-9]{1,2}$",
            PatternMessage = "1-90 arası tam sayı girin.",
            MaxLength = 2,
        },
        new(SettingKeys.RenewalReminderDays, "Sözleşme yenileme: hatırlatma eşikleri (gün)", "Sözleşmeler", SettingType.Text, "90,60,30,7")
        {
            Description = "Son bildirim tarihine (yoksa bitişe) bu kadar gün kala sorumluya takip aktivitesi açılır. Virgülle ayrılmış günler (ör. 90,60,30,7).",
            Pattern = @"^[0-9, ]{1,40}$",
            PatternMessage = "Virgülle ayrılmış gün sayıları girin (ör. 90,60,30,7).",
            MaxLength = 40,
        },
        new(SettingKeys.AllowAutoRenewal, "Sözleşme yenileme: otomatik uzatmaya izin ver", "Sözleşmeler", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse, yenileme yöntemi otomatik olan imzalı sözleşmeler son bildirim tarihi geçince ve dönem bitiminde yenileme süresi kadar kendiliğinden uzatılır. Hayır (varsayılan) iken hiçbir sözleşme kendiliğinden uzatılmaz.",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
        new(SettingKeys.ProviderName, "Sözleşme belgesi: hizmet veren ünvanı", "Sözleşmeler", SettingType.Text)
        {
            Description = "Belge başlığında ve imza alanında görünür; şablonda {{hizmet_veren.unvan}} değişkeniyle kullanılır.",
            MaxLength = 200,
        },
        new(SettingKeys.ProviderDetails, "Sözleşme belgesi: hizmet veren iletişim bilgileri", "Sözleşmeler", SettingType.MultilineText)
        {
            Description = "Belge başlığının altında küçük yazıyla görünür (adres, vergi no, telefon).",
            MaxLength = 500,
        },
        new(SettingKeys.FooterNote, "Sözleşme belgesi: alt bilgi notu", "Sözleşmeler", SettingType.Text)
        {
            Description = "Her sayfanın altında görünür (ör. gizlilik notu).",
            MaxLength = 200,
        },
    ];

    /// <summary>Devre dışı bırakılan kişinin yürürlükte olmayan ve yürürlükteki sözleşmeleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Contract, "sözleşme") { Path = "sozlesmeler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "contracts.manager",
            "Sözleşme sorumlusu",
            "Sözleşmeleri, taraflarını, kalemlerini ve tutarlarını görüntüler ve düzenler.",
            [Permissions.ContractsRead, Permissions.ContractsWrite, Permissions.FinancialsRead, Permissions.DocumentsGenerate, Permissions.DocumentsDownload, Permissions.CommitmentsWrite, Permissions.CommitmentsComplete])
        {
            Scope = RecordScope.Own,
        },
        new(
            "contracts.approver",
            "Sözleşme onaylayıcı",
            "Sözleşmeleri tutarlarıyla görüntüler, iç onayı verir ya da reddeder.",
            [Permissions.ContractsRead, Permissions.FinancialsRead, Permissions.ContractsApprove])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.template-manager",
            "Sözleşme şablon yöneticisi",
            "Sözleşme şablonlarını oluşturur, düzenler, yayımlar ve arşivler.",
            [Permissions.ContractsRead, Permissions.TemplatesEdit, Permissions.TemplatesPublish])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.approval-manager",
            "Sözleşme onay yöneticisi",
            "Sözleşme onay kurallarını ve vekâletlerini tanımlar; sözleşmeleri tutarlarıyla görüntüler.",
            [Permissions.ContractsRead, Permissions.FinancialsRead, Permissions.ApprovalsManage])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.signature-manager",
            "Sözleşme imza sorumlusu",
            "Onaylı sözleşmeyi imzaya gönderir, imza sürecini izler ve manuel imzayı doğrular.",
            [Permissions.ContractsRead, Permissions.SignaturesManage, Permissions.SignaturesVerify, Permissions.DocumentsDownload])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.renewal-manager",
            "Sözleşme yenileme sorumlusu",
            "Yenileme takvimini izler, yenileme taslağı açar, yenilememe kararı verir; fesih yetkisi içermez.",
            [Permissions.ContractsRead, Permissions.ContractsWrite, Permissions.FinancialsRead, Permissions.RenewalsManage])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.viewer",
            "Sözleşme izleyici",
            "Sözleşmeleri tutarlar olmadan görüntüler.",
            [Permissions.ContractsRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.ContractCommitment, "Sözleşme taahhüdü", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["contractId"] = "Sözleşme", ["title"] = "Başlık", ["description"] = "Açıklama", ["category"] = "Kategori", ["obligor"] = "Yükümlü taraf",
            ["responsibleUserId"] = "Sorumlu", ["escalationUserId"] = "Eskalasyon", ["kind"] = "Tür", ["startDate"] = "Başlangıç", ["dueDate"] = "Son tarih",
            ["recurrence"] = "Tekrar", ["recurrenceEndDate"] = "Tekrar bitişi", ["graceDays"] = "Tolerans süresi (gün)", ["escalateAfterDays"] = "Eskalasyon (gün)",
            ["metricName"] = "Ölçüt", ["unit"] = "Birim", ["direction"] = "Yön", ["targetValue"] = "Hedef", ["tolerance"] = "Tolerans",
            ["dependsOnCommitmentId"] = "Bağımlı olduğu taahhüt", ["consequenceNote"] = "Sonuç maddesi", ["status"] = "Durum",
        }, "title")
        {
            UserFields = ["responsibleUserId", "escalationUserId", "createdBy"],
            References = new Dictionary<string, string> { ["contractId"] = Entities.Contract, ["dependsOnCommitmentId"] = Entities.ContractCommitment },
        },
        new(Entities.ContractApprovalRule, "Sözleşme onay kuralı", Permissions.ApprovalsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["priority"] = "Öncelik", ["step"] = "Adım", ["isActive"] = "Etkin", ["contractType"] = "Sözleşme türü",
            ["currency"] = "Para birimi", ["minGrossTotal"] = "Tutar eşiği", ["minDiscountPercent"] = "İndirim eşiği %", ["minTermMonths"] = "Süre eşiği (ay)",
            ["requiresNonStandardTerms"] = "Standart dışı madde", ["minRiskLevel"] = "En düşük risk düzeyi", ["approverId"] = "Onaylayan",
        }, "name")
        {
            UserFields = ["approverId"],
        },
        new(Entities.ContractApprovalDelegation, "Sözleşme onay vekâleti", Permissions.ApprovalsManage, new Dictionary<string, string>
        {
            ["fromUserId"] = "Vekâlet veren", ["toUserId"] = "Vekil", ["startsAt"] = "Başlangıç", ["endsAt"] = "Bitiş", ["isActive"] = "Etkin", ["note"] = "Not",
        })
        {
            UserFields = ["fromUserId", "toUserId"],
        },
        new(Entities.ContractTemplate, "Sözleşme şablonu", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["language"] = "Dil", ["contractType"] = "Sözleşme türü", ["usageScope"] = "Kullanım kapsamı", ["description"] = "Açıklama",
            ["bodySha256"] = "Metin özeti", ["isExample"] = "Örnek metin", ["status"] = "Durum", ["publishedVersionNo"] = "Yayımlanan sürüm",
        }, "name"),
        new(Entities.Contract, "Sözleşme", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["number"] = "Numara", ["title"] = "Başlık", ["type"] = "Tür", ["description"] = "Açıklama", ["partyId"] = "Müşteri",
            ["personId"] = "İlgili kişi", ["ownerId"] = "Sorumlu", ["previousContractId"] = "Önceki sözleşme", ["startDate"] = "Başlangıç",
            ["endDate"] = "Bitiş", ["isIndefinite"] = "Belirsiz süreli", ["currency"] = "Para birimi", ["priceIncludesTax"] = "Fiyatlar vergi dahil",
            ["contractValue"] = "Sözleşme bedeli", ["paymentTerms"] = "Ödeme koşulları", ["renewalMode"] = "Yenileme yöntemi",
            ["renewalPeriodMonths"] = "Yenileme süresi (ay)", ["noticePeriodDays"] = "Bildirim süresi (gün)", ["noticeDeadline"] = "Son bildirim tarihi", ["escalationUserId"] = "Yenileme eskalasyonu",
            ["confidentiality"] = "Gizlilik", ["hasNonStandardTerms"] = "Standart dışı madde", ["riskLevel"] = "Risk düzeyi", ["preparationStatus"] = "Hazırlık durumu", ["approvalStatus"] = "Onay durumu",
            ["signatureStatus"] = "İmza durumu", ["effectiveStatus"] = "Yürürlük durumu", ["statusReason"] = "Gerekçe",
            ["currentVersionNo"] = "Güncel sürüm", ["effectiveVersionNo"] = "Yürürlükteki sürüm", ["netTotal"] = "Net toplam",
            ["taxTotal"] = "Vergi", ["grossTotal"] = "Genel toplam",
        }, "title")
        {
            UserFields = ["ownerId", "escalationUserId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person, ["previousContractId"] = Entities.Contract },
        },
        new(Entities.ContractParty, "Sözleşme tarafı", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["contractId"] = "Sözleşme", ["role"] = "Rol", ["partyId"] = "Taraf", ["personId"] = "Kişi", ["position"] = "Sıra", ["notes"] = "Not",
        })
        {
            References = new Dictionary<string, string> { ["contractId"] = Entities.Contract, ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person },
        },
        new(Entities.ContractLine, "Sözleşme kalemi", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["contractId"] = "Sözleşme", ["position"] = "Sıra", ["kind"] = "Tür", ["productCode"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama",
            ["quantity"] = "Miktar", ["unitName"] = "Birim", ["unitPrice"] = "Birim fiyat", ["discountPercent"] = "İndirim %",
            ["taxRatePercent"] = "Vergi %", ["billingPeriod"] = "Dönem", ["net"] = "Net", ["tax"] = "Vergi", ["gross"] = "Toplam",
        }, "name")
        {
            References = new Dictionary<string, string> { ["contractId"] = Entities.Contract },
        },
    ];

    /// <summary>Hassas alanlar (Y8b): bedel, fiyat ve toplamlar. Para birimi tek başına bir şey söylemediği için hassas değildir.</summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Contract, Permissions.FinancialsRead, ["contractValue", "netTotal", "taxTotal", "grossTotal", "recurringSummary"]),
        new(Entities.ContractLine, Permissions.FinancialsRead, ["unitPrice", "discountPercent", "listAmount", "lineDiscount", "net", "tax", "gross"]),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Contract, "Sözleşme"),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => ContractsLookupDefinitions.All;

    /// <summary>Sözleşme formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021 form düzeni).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => ContractsForms.All;

    /// <summary>Sözleşme türü (Y9b): hazır değerler örnektir; firma kendi türlerini ekler. Kod türe bakmaz (sektör koşulu yok).</summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.ContractType,
            "Sözleşme türü",
            Entities.Contract,
            "type",
            Required: true,
            [],
            [
                new("Service", "Hizmet sözleşmesi"),
                new("Sales", "Satış sözleşmesi"),
                new("Framework", "Çerçeve sözleşme"),
                new("Maintenance", "Bakım / destek sözleşmesi"),
                new("Subscription", "Abonelik sözleşmesi"),
                new("Nda", "Gizlilik sözleşmesi"),
                new("Other", "Diğer"),
            ])
        {
            Default = "Service",
        },
        new(
            Lists.CommitmentCategory,
            "Taahhüt kategorisi",
            Entities.ContractCommitment,
            "category",
            Required: true,
            [],
            [
                new("Delivery", "Teslimat"),
                new("Training", "Eğitim"),
                new("Installation", "Kurulum"),
                new("Documents", "Belge sağlama"),
                new("Reporting", "Raporlama"),
                new("ServiceLevel", "Hizmet seviyesi"),
                new("Payment", "Ödeme"),
                new("Other", "Diğer"),
            ])
        {
            Default = "Delivery",
        },
    ];

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): müşteri ve kişi, sözleşme okuma izniyle okunur; sözleşme yazma izni yeni müşteri/kişi açabilir (müşteri
    /// herhangi bir modülle başlayabilir; CRM kapalı tenant'ta da sözleşme müşterisi girilebilmelidir). CRM uzantısı (firma kartı) bu
    /// yolla oluşmaz; CRM açıksa aynı taraf orada da kullanılır.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.ContractsRead, Permissions.ContractsWrite),
        new(SharedEntities.Person, Permissions.ContractsRead, Permissions.ContractsWrite),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Contract, "partyId", "sözleşme"),
        new(SharedEntities.Person, Entities.Contract, "personId", "sözleşme"),
        new(SharedEntities.Party, Entities.ContractParty, "partyId", "sözleşme tarafı"),
        new(SharedEntities.Person, Entities.ContractParty, "personId", "sözleşme tarafı"),
    ];

    /// <summary>
    /// Sistem kimlikleri (oturumsuz çağıranlar): imza sağlayıcısının webhook çağrısı bu kimlikle, kendi tenant kapsamında ve yalnızca bildirilen izinlerle çalışır.
    /// Olay işlemez; izinlerini tanımlar.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        EventSubscription.For<ContractSystemAnchor>(
            "contracts.signature-webhook",
            [DomainEventTypes.RecordDeleted],
            [Entities.Contract],
            [Permissions.ContractsRead, Permissions.SignaturesProviderEvents]),
        // Zamanlanmış işlerin sistem kimliği (yenileme hatırlatması, otomatik uzatma, yürürlük geçişi, imza ve taahhüt süreleri): olay işlemez, izinlerini tanımlar.
        EventSubscription.For<ContractSchedulerAnchor>(
            "contracts.scheduler",
            [DomainEventTypes.RecordDeleted],
            [Entities.Contract],
            [Permissions.ContractsRead, Permissions.ContractsWrite, Permissions.RenewalsManage, Permissions.SignaturesManage, Permissions.CommitmentsWrite]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ContractsStoreContext>();
        services.AddScoped<ContractWorkflowGate>();
        services.AddScoped<ContractReader>();
        services.AddScoped<IRecordStore, ContractRecordStore>();
        services.AddScoped<IRecordStore, ContractPartyRecordStore>();
        services.AddScoped<IRecordStore, ContractLineRecordStore>();
        services.AddScoped<IContractWorkflow, ContractWorkflow>();
        services.AddScoped<IContractFromQuote, ContractFromQuote>();
        services.AddScoped<IRecordStore, ContractTemplateRecordStore>();
        services.AddScoped<IContractTemplates, ContractTemplates>();
        services.AddScoped<IContractDocuments, ContractDocuments>();
        services.AddScoped<IRecordStore, ContractApprovalRuleRecordStore>();
        services.AddScoped<IRecordStore, ContractApprovalDelegationRecordStore>();
        services.AddScoped<IRecordStore, ContractCommitmentRecordStore>();
        services.AddScoped<IContractCommitments, ContractCommitments>();
        services.AddScoped<IContractRenewals, ContractRenewals>();
        services.AddScoped<IContractScheduler, ContractScheduler>();
        services.AddScoped<ContractSchedulerAnchor>();
        services.AddScoped<IContractApprovals, ContractApprovals>();
        services.AddScoped<ContractApprovalEngine>();
        services.AddSingleton<IContractSignatureProvider, SimulationSignatureProvider>();
        services.AddScoped<IContractSignatureProviders, ContractSignatureProviders>();
        services.AddScoped<IContractSignatures, ContractSignatures>();
        services.AddScoped<ContractSystemAnchor>();
        services.AddScoped<IActivityLinkTarget, ContractLinkTarget>();
    }
}
