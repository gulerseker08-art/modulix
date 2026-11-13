using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Approvals;
using Modulix.Core.Automation;
using Modulix.Core.Events;
using Modulix.Core.Settings;

namespace Modulix.Core.Modules;

/// <summary>Platformun kendi izinleri. Core modülü her tenant için açıktır, kapatılamaz.</summary>
public sealed class CoreModule : IModule
{
    public const string ModuleKey = "core";

    public static class Permissions
    {
        /// <summary>Kullanıcıları ve üyeliklerin rollerini yönetir.</summary>
        public const string ManageUsers = "core.users.manage";

        public const string ManageRoles = "core.roles.manage";

        /// <summary>Kuruluş ve modül ayarlarını yazar; okumak için izin gerekmez.</summary>
        public const string ManageSettings = "core.settings.manage";

        public const string ReadAudit = "core.audit.read";

        /// <summary>Özelleştirme (Y9): özel alanlar, listeler ve sektör profili.</summary>
        public const string ManageCustomization = "core.customization.manage";

        /// <summary>İşletici şirketleri ve kullanıcıların şirket görevlerini yönetir (ADR-0008).</summary>
        public const string ManageCompanies = "core.companies.manage";

        /// <summary>İletişim tercihlerini, izin geçmişini ve engelleri okur (ADR-0025).</summary>
        public const string ReadCommunication = "core.communication.read";

        /// <summary>Kanıtla izin/ret kaydeder (ADR-0025). Engel koymak ve kaldırmak ayrı izindir.</summary>
        public const string ManageCommunication = "core.communication.manage";

        /// <summary>İletişim noktasını engeller ve engeli gerekçeyle kaldırır (ADR-0025).</summary>
        public const string SuppressCommunication = "core.communication.suppress";

        /// <summary>Kural altyapısı (ADR-0027): kuralları, şablonları ve yetenek kataloğunu görüntüler.</summary>
        public const string ReadAutomation = "core.automation.read";

        /// <summary>Kural taslağı oluşturur, düzenler, siler ve yan etkisiz önizler. Yayınlamak ayrı izindir.</summary>
        public const string ManageAutomation = "core.automation.manage";

        /// <summary>Kuralı yayınlar, açar ve kapatır. Yayınlayan, sahip olmadığı izni gerektiren aksiyonu yayınlayamaz.</summary>
        public const string PublishAutomation = "core.automation.publish";

        /// <summary>Kuralı elle çalıştırır (ADR-0027 Dilim 2).</summary>
        public const string RunAutomation = "core.automation.run";

        /// <summary>Otomasyon çalışma geçmişini görüntüler (ADR-0027 Dilim 2).</summary>
        public const string ReadAutomationLogs = "core.automation-logs.read";

        /// <summary>Otomasyonun bekleyen onaylarını karara bağlar (ADR-0027 Dilim 4).</summary>
        public const string ApproveAutomation = "core.automation.approve";

        /// <summary>Onay talebi açar, kendi taleplerini görür ve geri çeker (ADR-0027 Dilim 4a).</summary>
        public const string RequestApprovals = "core.approvals.request";

        /// <summary>Onaycısı olduğu adımlara karar verir; izin tek başına yetmez, kişi adımın onaycısı olmalıdır.</summary>
        public const string DecideApprovals = "core.approvals.decide";

        /// <summary>Onay süreçlerini tanımlar, bütün talepleri görür ve sahipsiz adımın onaycısını değiştirir.</summary>
        public const string ManageApprovals = "core.approvals.manage";

        /// <summary>Rapor merkezini açar, hazır ve paylaşılan raporları çalıştırır (ADR-0035, dilim 3). Veri erişimi vermez: her rapor çalıştıranın kayıt yetkisiyle çalışır.</summary>
        public const string ReadReports = "core.reports.read";

        /// <summary>Kendi raporlarını oluşturur, düzenler ve siler; rapor oluşturucuyu kullanır.</summary>
        public const string ManageReports = "core.reports.manage";

        /// <summary>Kendi raporunu firmayla ya da seçilen kullanıcılarla paylaşır. Paylaşım veri erişimi kazandırmaz.</summary>
        public const string ShareReports = "core.reports.share";

        /// <summary>Rapor sonucunu CSV olarak dışa aktarır (ADR-0035, dilim 4). Dosya yalnızca sahibine, indirme anında yetki yeniden denetlenerek verilir.</summary>
        public const string ExportReports = "core.reports.export";

        /// <summary>Kendi raporlarını zamanlar; zamanlanmış rapor çalışırken sahibinin yetkisi yeniden denetlenir, yetki kaybında teslim durur.</summary>
        public const string ScheduleReports = "core.reports.schedule";

        /// <summary>İçe aktarma başlatır; işlem günlüğünde yalnızca kendi içe aktarmalarını görür (ADR-0033).</summary>
        public const string RunImports = "core.imports.run";

        /// <summary>İşlem günlüğünde firmanın bütün içe aktarmalarını ve satır hatalarını görür (ADR-0033).</summary>
        public const string ReadImports = "core.imports.read";

        /// <summary>İçe aktarılmış kayıtları gerekçeyle toplu geri alır (ADR-0033, Dilim 4).</summary>
        public const string RollbackImports = "core.imports.rollback";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Çekirdek";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ManageUsers, "Kullanıcıları ve kullanıcıların rollerini yönetir", "Kullanıcılar"),
        new(Permissions.ManageRoles, "Rolleri ve izinlerini yönetir", "Roller"),
        new(Permissions.ManageSettings, "Kuruluş ve modül ayarlarını değiştirir", "Kuruluş ayarları"),
        new(Permissions.ReadAudit, "Denetim kaydını görüntüler", "Denetim kaydı"),
        new(Permissions.ManageCustomization, "Özel alanları ve listeleri düzenler", "Özelleştirme"),
        new(Permissions.ManageCompanies, "İşletici şirketleri ve kullanıcıların şirket görevlerini yönetir", "Şirketler"),
        new(Permissions.ReadCommunication, "İletişim tercihlerini, izin geçmişini ve engelleri görüntüler", "İletişim tercihleri"),
        new(Permissions.ManageCommunication, "İletişim izni ve ret kaydeder (kanıtla)", "İletişim tercihleri"),
        new(Permissions.SuppressCommunication, "İletişim noktasını engeller ve engeli gerekçeyle kaldırır", "İletişim tercihleri"),
        new(Permissions.ReadAutomation, "Otomasyon kurallarını ve yeteneklerini görüntüler", "Otomasyon"),
        new(Permissions.ManageAutomation, "Otomasyon kuralı taslağı oluşturur, düzenler ve önizler", "Otomasyon"),
        new(Permissions.PublishAutomation, "Otomasyon kuralını yayınlar, açar ve kapatır", "Otomasyon"),
        new(Permissions.RunAutomation, "Otomasyon kuralını elle çalıştırır", "Otomasyon"),
        new(Permissions.ReadAutomationLogs, "Otomasyon çalışma geçmişini görüntüler", "Otomasyon geçmişi"),
        new(Permissions.ApproveAutomation, "Otomasyonun bekleyen onaylarını karara bağlar", "Otomasyon"),
        new(Permissions.RequestApprovals, "Onay talebi açar ve kendi taleplerini izler", "Onaylar"),
        new(Permissions.DecideApprovals, "Onaycısı olduğu adımlara karar verir", "Onaylar"),
        new(Permissions.ManageApprovals, "Onay süreçlerini tanımlar ve bütün talepleri yönetir", "Onaylar"),
        new(Permissions.ReadReports, "Rapor merkezini açar ve raporları çalıştırır", "Raporlar"),
        new(Permissions.ManageReports, "Kendi raporlarını oluşturur, düzenler ve siler", "Raporlar"),
        new(Permissions.ShareReports, "Kendi raporunu firmayla ya da kullanıcılarla paylaşır", "Raporlar"),
        new(Permissions.ExportReports, "Rapor sonuçlarını CSV olarak dışa aktarır", "Raporlar"),
        new(Permissions.ScheduleReports, "Kendi raporlarını zamanlar", "Raporlar"),
        new(Permissions.RunImports, "İçe aktarma başlatır ve kendi içe aktarmalarını görür", "İçe aktarma"),
        new(Permissions.ReadImports, "Firmanın bütün içe aktarmalarını ve satır hatalarını görüntüler", "İçe aktarma"),
        new(Permissions.RollbackImports, "İçe aktarılmış kayıtları toplu geri alır", "İçe aktarma"),
    ];

    /// <summary>Çekirdeğin olay abonelikleri (ADR-0007).</summary>
    public static class Subscriptions
    {
        /// <summary>
        /// Kayıt başkasına atanınca yeni sorumluya e-posta. Kayıt türleri, modüllerin <see cref="IModule.OwnedRecords"/>
        /// ile bildirdiği türlerdir (katalog doldurur); yeni modül ek iş yapmadan katılır.
        /// </summary>
        public const string AssignmentEmail = "core.assignment-email";

        /// <summary>
        /// Kural altyapısı (ADR-0027, Dilim 2): kayıt olayını kuralların tetikleyicisiyle eşler ve çalışma satırı yazar. Kayıt türleri,
        /// modüllerin <see cref="IModule.Automation"/> ile bildirdiği türlerdir (katalog doldurur); hiçbiri yoksa abonelik de yoktur.
        /// </summary>
        public const string Automation = "core.automation";

        /// <summary>Kayda bağlı onayın bütünlüğü (ADR-0027 Dilim 4c): korunan alan değişince bekleyen talep geçersizleşir.</summary>
        public const string Approvals = "core.approvals";
    }

    /// <summary>Denetim kayıt türleri (Y5). Yönetim kayıtlarının değerleri her denetim okuruna açıktır.</summary>
    public static class AuditedEntityKeys
    {
        public const string Role = "core.role";

        /// <summary>Ekip (Y8): ad, açıklama ve üyeler.</summary>
        public const string Team = "core.team";

        /// <summary>Özel alan tanımı (Y9a); kaydın kimliği alanın kimliğidir.</summary>
        public const string CustomField = "core.custom-field";

        /// <summary>Seçim listesi (Y9b); kaydın kimliği firmanın liste satırının kimliğidir.</summary>
        public const string ChoiceList = "core.choice-list";

        /// <summary>Lookup ayarı (ADR-0016); kaydın kimliği firmanın ayar satırının kimliğidir.</summary>
        public const string LookupSetting = "core.lookup-setting";

        /// <summary>Form düzeni (ADR-0021); kaydın kimliği firmanın düzen satırının kimliğidir.</summary>
        public const string FormLayout = "core.form-layout";

        /// <summary>İçe aktarma tasarımı (ADR-0033); kaydın kimliği firmanın tasarım satırının kimliğidir.</summary>
        public const string ImportDesign = "core.import-design";

        /// <summary>Kayda bağlı dosya (ADR-0040); kaydın kimliği dosyanın kimliğidir.</summary>
        public const string File = "core.file";

        public const string Membership = "core.membership";

        /// <summary>İşletici şirket (ADR-0008): ad ve varsayılan olup olmadığı; kaydın kimliği şirketin kimliğidir.</summary>
        public const string Company = "core.company";

        /// <summary>Şube (ADR-0008): ad ve bağlı olduğu şirket; kaydın kimliği şubenin kimliğidir.</summary>
        public const string Branch = "core.branch";

        /// <summary>Departman (ADR-0026): ad, kod, tür, üst departman ve açık/kapalı; kaydın kimliği departmanın kimliğidir.</summary>
        public const string BusinessUnit = "core.business-unit";

        /// <summary>Otomasyon kuralı (ADR-0027); kaydın kimliği kuralın kimliğidir. Tanımın okunabilir özeti yazılır.</summary>
        public const string AutomationRule = "core.automation-rule";

        /// <summary>Onay süreci tanımı (ADR-0027 Dilim 4a); kaydın kimliği tanımın kimliğidir.</summary>
        public const string ApprovalDefinition = "core.approval-definition";

        /// <summary>Onay talebi (ADR-0027 Dilim 4a); kaydın kimliği talebin kimliğidir. Karar, not ve atama değişikliği yazılır.</summary>
        public const string ApprovalRequest = "core.approval-request";

        /// <summary>Onay vekâleti (ADR-0027 Dilim 4b); kaydın kimliği vekâletin kimliğidir.</summary>
        public const string ApprovalDelegation = "core.approval-delegation";

        /// <summary>Kayıtlı rapor (ADR-0035, dilim 3); kaydın kimliği raporun kimliğidir. Tanımın okunabilir özeti yazılır.</summary>
        public const string Report = "core.report";

        /// <summary>Zamanlanmış rapor (ADR-0035, dilim 4); kaydın kimliği zamanlamanın kimliğidir.</summary>
        public const string ReportSchedule = "core.report-schedule";

        /// <summary>Tenant'ın faaliyet seçimi (ADR-0029); kaydın kimliği tenant'ın kimliğidir, seçimin tamamı bir kayıt olarak yazılır.</summary>
        public const string ActivitySelection = "core.activity-selection";

        /// <summary>Tenant'ın alan kuralı uyarlaması (ADR-0029 K8); kaydın kimliği tenant'ın kimliğidir. Kural anahtarları yazılır.</summary>
        public const string RuleAdaptation = "core.rule-adaptation";

        /// <summary>Firma alt profili (ADR-0041); seçenekleri aynı kaydın "options" alanında listelenir.</summary>
        public const string SubProfile = "core.sub-profile";

        public const string Tenant = "core.tenant";
        public const string TenantSetting = "core.tenant-setting";
        public const string Audit = "core.audit";

        /// <summary>İletişim tercihi (ADR-0025); kaydın kimliği tercih satırının kimliğidir. Adres maskeli yazılır.</summary>
        public const string CommunicationPreference = "core.communication-preference";

        /// <summary>İletişim engeli (ADR-0025); kaydın kimliği engel satırının kimliğidir. Adres maskeli yazılır.</summary>
        public const string CommunicationSuppression = "core.communication-suppression";

        /// <summary>İletişim konusu tanımı (ADR-0025, dilim 14); kaydın kimliği konu satırının kimliğidir.</summary>
        public const string CommunicationTopic = "core.communication-topic";

        /// <summary>Konu çıkışı/dönüşü (ADR-0025, dilim 14); kaydın kimliği tercih satırının kimliğidir. Adres maskeli yazılır.</summary>
        public const string CommunicationTopicPreference = "core.communication-topic-preference";

        /// <summary>Oturum olayları (giriş, hatalı şifre, kilitlenme, şifre sıfırlama, davet kabulü); kayıt kişinin hesabıdır.</summary>
        public const string Account = "core.account";

        /// <summary>
        /// Olay teslimi (ADR-0007): platform yöneticisinin yeniden deneme ve atlama işlemleri; kaydın kimliği teslimin kimliğidir.
        /// </summary>
        public const string EventDelivery = "core.event-delivery";
    }

    /// <summary>Çekirdeğin firma ayarları (Y6): firma profili ve bölgesel ayarlar.</summary>
    public static class SettingKeys
    {
        public const string DisplayName = "core.profile.display-name";
        public const string LegalName = "core.profile.legal-name";
        public const string TaxOffice = "core.profile.tax-office";
        public const string TaxNumber = "core.profile.tax-number";
        public const string Address = "core.profile.address";
        public const string Phone = "core.profile.phone";
        public const string Email = "core.profile.email";
        public const string TimeZone = "core.regional.time-zone";
        public const string Currency = "core.regional.currency";
        public const string Format = "core.regional.format";
        public const string Language = "core.regional.language";
        public const string RequireTwoFactor = "core.security.require-two-factor";

        /// <summary>Acil durdurma (ADR-0027): "Hayır" ise firmanın hiçbir kuralı çalışmaz; kuyruktaki çalışmalar iptal edilir.</summary>
        public const string AutomationEnabled = "core.automation.enabled";

        /// <summary>Yapay zekâ erişimi: "Hayır" ise firmada yapay zekâ özellikleri kapalıdır (varsayılan kapalı; açık rıza).</summary>
        public const string AiEnabled = "core.ai.enabled";
    }

    /// <summary>Evet/Hayır ayarlarının değerleri.</summary>
    public static class SettingValues
    {
        public const string Yes = "true";
        public const string No = "false";
    }

    /// <summary>İletişim tercihi denetim değerlerini (maskeli adres, kanıt) yalnızca bu izni olan denetim okuru görür.</summary>
    private const string ReadCommunicationKey = Permissions.ReadCommunication;

    private const string ProfileGroup = "Kuruluş profili";
    private const string RegionalGroup = "Bölgesel ayarlar";
    private const string SecurityGroup = "Güvenlik";
    private const string AutomationGroup = "Otomasyon";
    private const string AiGroup = "Yapay zekâ";

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.DisplayName, "Görünen ad", ProfileGroup, SettingType.Text)
        {
            Description = "Üst çubukta firmanın adı yerine gösterilir. Boşsa firmanın kayıtlı adı kullanılır.",
            MaxLength = 100,
        },
        new(SettingKeys.LegalName, "Ticari unvan", ProfileGroup, SettingType.Text) { MaxLength = 250 },
        new(SettingKeys.TaxOffice, "Vergi dairesi", ProfileGroup, SettingType.Text) { MaxLength = 100 },
        new(SettingKeys.TaxNumber, "Vergi no", ProfileGroup, SettingType.Text)
        {
            MaxLength = 11,
            Pattern = "^[0-9]{10,11}$",
            PatternMessage = "Vergi numarası 10, T.C. kimlik numarası 11 rakamdır.",
        },
        new(SettingKeys.Address, "Adres", ProfileGroup, SettingType.MultilineText) { MaxLength = 500 },
        new(SettingKeys.Phone, "Telefon", ProfileGroup, SettingType.Phone) { MaxLength = 30 },
        new(SettingKeys.Email, "E-posta", ProfileGroup, SettingType.Email),
        new(SettingKeys.TimeZone, "Saat dilimi", RegionalGroup, SettingType.Choice, RegionalOptions.DefaultTimeZone)
        {
            Description = "Ekranlardaki tarih ve saatler, denetim kaydı ve dışa aktarmalar bu saat dilimini kullanır.",
            Options = RegionalOptions.TimeZones,
        },
        new(SettingKeys.Currency, "Para birimi", RegionalGroup, SettingType.Choice, RegionalOptions.DefaultCurrency)
        {
            Description = "Yeni kayıtlarda varsayılan para birimi. Mevcut kayıtların para birimi değişmez.",
            Options = RegionalOptions.Currencies,
        },
        new(SettingKeys.Format, "Tarih ve sayı biçimi", RegionalGroup, SettingType.Choice, RegionalOptions.DefaultFormat)
        {
            Options = RegionalOptions.Formats,
        },
        new(SettingKeys.Language, "Arayüz dili", RegionalGroup, SettingType.Choice, LanguageOptions.Default)
        {
            Description = "Firmanın varsayılan dili. Dilini kendi hesabında seçmiş kullanıcılar kendi dilini görür. Kayıtlı veriler değişmez.",
            Options = LanguageOptions.Languages,
        },

        // Y7b: açıkken iki adımlı doğrulaması kapalı biri bu firmanın ekranlarına giremez, önce kurulumu yapar.
        new(SettingKeys.RequireTwoFactor, "İki adımlı doğrulama zorunlu", SecurityGroup, SettingType.Choice, SettingValues.No)
        {
            Description = "Açıksa iki adımlı doğrulaması kapalı kullanıcılar firmaya girmeden önce doğrulama uygulamasını kurar. "
                + "Açabilmek için sizin iki adımlı doğrulamanız açık olmalı.",
            Options = [new(SettingValues.No, "Hayır"), new(SettingValues.Yes, "Evet")],
        },

        new(SettingKeys.AutomationEnabled, "Otomasyon kuralları çalışsın", AutomationGroup, SettingType.Choice, SettingValues.Yes)
        {
            Description = "Hayır derseniz bu firmanın hiçbir otomasyon kuralı çalışmaz ve kuyruktaki çalışmalar iptal edilir; kurallar silinmez. "
                + "Beklenmedik bir davranışta hızlıca durdurmak içindir.",
            Options = [new(SettingValues.No, "Hayır"), new(SettingValues.Yes, "Evet")],
        },

        new(SettingKeys.AiEnabled, "Yapay zekâ özellikleri açık", AiGroup, SettingType.Choice, SettingValues.No)
        {
            Description = "Açıksa firmanın yapay zekâ özellikleri kullanılabilir. Yapay zekâ yalnızca kullanıcının kendi yetkisi kadar veriye erişir "
                + "ve yaptığı her işlem denetim kaydına yazılır. Kapalıyken hiçbir veri yapay zekâya gönderilmez.",
            Options = [new(SettingValues.No, "Hayır"), new(SettingValues.Yes, "Evet")],
        },
    ];

    /// <summary>
    /// Süreli belge türü (ADR-0038 K3, Y9b): hazır değerler örnektir; tenant kendi türlerini ekler, yeni türde kategori seçilir. Kategori
    /// belgenin hangi kayda eklenebileceğini belirler (araç, sürücü, firma, tesis, çalışan; sahip türünü modül bildirir).
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            CredentialEntities.TypeList,
            "Süreli belge türü",
            CredentialEntities.Credential,
            "typeKey",
            Required: true,
            [
                new(CredentialCategories.Vehicle, "Araç belgesi"),
                new(CredentialCategories.Driver, "Sürücü belgesi"),
                new(CredentialCategories.Party, "Firma belgesi"),
                new(CredentialCategories.Facility, "Tesis belgesi"),
                new(CredentialCategories.Employee, "Özlük belgesi"),
            ],
            [
                new("Inspection", "Araç muayenesi", CredentialCategories.Vehicle),
                new("TrafficInsurance", "Trafik sigortası", CredentialCategories.Vehicle),
                new("Casco", "Kasko", CredentialCategories.Vehicle),
                new("ExhaustEmission", "Egzoz emisyon ölçümü", CredentialCategories.Vehicle),
                new("TachographCalibration", "Takograf kalibrasyonu", CredentialCategories.Vehicle),
                new("DrivingLicense", "Sürücü belgesi (ehliyet)", CredentialCategories.Driver),
                new("Src", "SRC belgesi", CredentialCategories.Driver),
                new("Psychotechnical", "Psikoteknik belgesi", CredentialCategories.Driver),
                new("AdrCertificate", "ADR sertifikası", CredentialCategories.Driver),
                new("TachographCard", "Takograf sürücü kartı", CredentialCategories.Driver),
                new("AuthorizationCertificate", "Yetki belgesi", CredentialCategories.Party),
                new("PowerOfAttorney", "Vekâletname", CredentialCategories.Party),
                new("OperatingPermit", "Faaliyet izni", CredentialCategories.Facility),
                new("EmploymentContract", "İş sözleşmesi", CredentialCategories.Employee),
                new("HealthReport", "Sağlık raporu", CredentialCategories.Employee),
                new("Diploma", "Diploma", CredentialCategories.Employee),
                new("TrainingCertificate", "Eğitim sertifikası", CredentialCategories.Employee),
                new("OhsTraining", "İş sağlığı ve güvenliği eğitimi", CredentialCategories.Employee),
                new("WorkPermit", "Çalışma izni", CredentialCategories.Employee),
            ]),
    ];

    /// <summary>Core lookup'ları: kural aksiyonlarında üye seçimi ("Bildirim gönder" alıcısı).</summary>
    IReadOnlyList<LookupDefinition> IModule.Lookups { get; } =
    [
        new(NotifyAutomation.RecipientLookup, "core.notification", "userId", "Alıcı", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
    ];

    /// <summary>Süreli belge formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms { get; } =
    [
        new(CredentialEntities.Credential, "Süreli belge",
        [new(FormDefinition.DefaultTab, "Belge"), new("notlar", "Notlar")],
        [
            new("belge", "Belge", "file-text"),
            new("gecerlilik", "Geçerlilik", "calendar"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(FormDefinition.ExtrasSection, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("typeKey", "Belge türü", "belge", 6, Required: true, Fixed: true),
            new("documentNo", "Belge no", "belge", 6),
            new("issuedBy", "Veren kurum", "belge", 8),
            new("status", "Durum", "belge", 4, Fixed: true),
            new("validFrom", "Başlangıç", "gecerlilik", 6),
            new("validTo", "Bitiş", "gecerlilik", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(CredentialEntities.Credential, "Süreli belge", null, new Dictionary<string, string>
        {
            ["ownerEntityKey"] = "Sahip kayıt türü", ["ownerId"] = "Sahip kayıt", ["typeKey"] = "Belge türü", ["documentNo"] = "Belge no",
            ["issuedBy"] = "Veren kurum", ["validFrom"] = "Başlangıç", ["validTo"] = "Bitiş", ["status"] = "Durum", ["notes"] = "Notlar",
        }, "typeKey")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { [CredentialStatuses.Active] = "Geçerli", [CredentialStatuses.Revoked] = "İptal edildi" },
            },
        },
        new(AuditedEntityKeys.File, "Dosya", null, new Dictionary<string, string>
        {
            ["fileName"] = "Dosya adı", ["contentType"] = "Tür", ["sizeBytes"] = "Boyut (bayt)", ["sha256"] = "SHA-256 özeti",
            ["entityKey"] = "Bağlı kayıt türü", ["recordId"] = "Bağlı kayıt",
        }, "fileName"),
        new(AuditedEntityKeys.ImportDesign, "İçe aktarma tasarımı", null, new Dictionary<string, string>
        {
            ["entityKey"] = "Kayıt türü", ["enabled"] = "Açık", ["runAutomation"] = "Kurallar ve atama e-postaları çalışır",
            ["excluded"] = "Çıkarılan sütunlar", ["required"] = "Zorunlu sütunlar",
        }, "entityKey"),
        new(AuditedEntityKeys.Role, "Rol", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["permissions"] = "İzinler", ["isSystem"] = "Sistem rolü", ["scope"] = "Kayıt kapsamı",
            ["assignLevel"] = "Atanabilir düzey", ["hideOutOfScopeShared"] = "Kapsam dışı ortak kayıtları gizle",
            ["scopeOverrides"] = "Kayıt türüne göre kapsam",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["scope"] = new Dictionary<string, string>
                {
                    ["All"] = "Tüm kayıtlar", ["UnitAndBelow"] = "Departmanın ve alt departmanların kayıtları",
                    ["Unit"] = "Departmanın kayıtları", ["Team"] = "Ekibinin kayıtları", ["Own"] = "Yalnızca kendi kayıtları",
                },
                ["assignLevel"] = new Dictionary<string, string>
                {
                    ["User"] = "Yalnızca kullanıcıya", ["Unit"] = "Yalnızca departmana", ["Both"] = "Kullanıcıya ve departmana",
                },
            },
        },
        new(AuditedEntityKeys.Team, "Ekip", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["members"] = "Üyeler",
        }, "name"),
        new(AuditedEntityKeys.CommunicationPreference, "İletişim tercihi", ReadCommunicationKey, new Dictionary<string, string>
        {
            ["channel"] = "Kanal", ["purpose"] = "Amaç", ["address"] = "Adres (maskeli)", ["status"] = "Durum", ["source"] = "Kaynak",
            ["evidenceRef"] = "Kanıt", ["reason"] = "Gerekçe",
        }, "address")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Unknown"] = "Bilinmiyor", ["Granted"] = "İzin var", ["Denied"] = "Reddedildi" },
                ["channel"] = new Dictionary<string, string> { ["Email"] = "E-posta", ["Phone"] = "Telefon", ["Sms"] = "SMS", ["WhatsApp"] = "WhatsApp" },
                ["purpose"] = new Dictionary<string, string> { ["Marketing"] = "Pazarlama", ["Transactional"] = "İşlem", ["Service"] = "Hizmet" },
            },
        },
        new(AuditedEntityKeys.CommunicationSuppression, "İletişim engeli", ReadCommunicationKey, new Dictionary<string, string>
        {
            ["channel"] = "Kanal", ["address"] = "Adres (maskeli)", ["reason"] = "Neden", ["source"] = "Kaynak", ["note"] = "Not",
            ["endsAt"] = "Bitiş", ["liftReason"] = "Kaldırma gerekçesi", ["lifted"] = "Kaldırıldı",
        }, "address")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["reason"] = new Dictionary<string, string>
                {
                    ["HardBounce"] = "Kalıcı teslim hatası", ["Complaint"] = "Şikâyet", ["InvalidAddress"] = "Geçersiz adres",
                    ["OptOut"] = "İletişim istemiyor", ["Manual"] = "Elle engel",
                },
                ["channel"] = new Dictionary<string, string> { ["Email"] = "E-posta", ["Phone"] = "Telefon", ["Sms"] = "SMS", ["WhatsApp"] = "WhatsApp" },
            },
        },
        new(AuditedEntityKeys.CommunicationTopic, "İletişim konusu", ReadCommunicationKey, new Dictionary<string, string>
        {
            ["key"] = "Anahtar", ["name"] = "Ad", ["active"] = "Açık",
        }, "name"),
        new(AuditedEntityKeys.CommunicationTopicPreference, "Konu tercihi", ReadCommunicationKey, new Dictionary<string, string>
        {
            ["channel"] = "Kanal", ["address"] = "Adres (maskeli)", ["topic"] = "Konu", ["optedOut"] = "Konudan çıktı", ["source"] = "Kaynak",
        }, "address")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["channel"] = new Dictionary<string, string> { ["Email"] = "E-posta", ["Phone"] = "Telefon", ["Sms"] = "SMS", ["WhatsApp"] = "WhatsApp" },
            },
        },
        new(AuditedEntityKeys.CustomField, "Özel alan", null, new Dictionary<string, string>
        {
            ["entity"] = "Kayıt türü", ["label"] = "Ad", ["key"] = "Anahtar", ["type"] = "Tip", ["required"] = "Zorunlu",
            ["showInList"] = "Listede göster", ["options"] = "Seçenekler", ["archived"] = "Arşivlendi",
        }, "label")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["type"] = new Dictionary<string, string>
                {
                    ["Text"] = "Metin", ["MultilineText"] = "Çok satırlı metin", ["Number"] = "Sayı", ["Date"] = "Tarih",
                    ["YesNo"] = "Evet/hayır", ["Choice"] = "Seçim listesi", ["MultiChoice"] = "Çoklu seçim",
                },
            },
        },
        new(AuditedEntityKeys.ChoiceList, "Liste", null, new Dictionary<string, string>
        {
            ["list"] = "Liste", ["values"] = "Değerler",
        }, "list"),
        new(AuditedEntityKeys.FormLayout, "Form düzeni", null, new Dictionary<string, string>
        {
            ["form"] = "Form", ["tabs"] = "Sekmeler", ["sections"] = "Bölümler", ["required"] = "Zorunlu alanlar", ["hidden"] = "Gizli alanlar", ["source"] = "Düzen kaynağı",
        }, "form"),
        new(AuditedEntityKeys.LookupSetting, "Lookup ayarı", null, new Dictionary<string, string>
        {
            ["lookup"] = "Alan", ["searchable"] = "Aranabilir", ["source"] = "Ayar kaynağı",
        }, "lookup"),
        new(AuditedEntityKeys.Company, "Şirket", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["isDefault"] = "Varsayılan",
        }, "name"),
        new(AuditedEntityKeys.Branch, "Şube", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["company"] = "Şirket",
        }, "name"),
        new(AuditedEntityKeys.BusinessUnit, "Departman", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["code"] = "Kod", ["kind"] = "Tür", ["parent"] = "Üst departman", ["isActive"] = "Etkin",
            ["roles"] = "Roller", ["affectedUsers"] = "Etkilenen kullanıcı",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["kind"] = new Dictionary<string, string> { ["Center"] = "Merkez", ["Dealer"] = "Bayi", ["Other"] = "Diğer" },
            },
        },
        new(AuditedEntityKeys.AutomationRule, "Otomasyon kuralı", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["module"] = "Modül", ["entity"] = "Kayıt türü", ["status"] = "Durum", ["priority"] = "Öncelik",
            ["stopProcessing"] = "Sonraki kuralları durdur", ["activeFrom"] = "Etkinlik başlangıcı", ["activeUntil"] = "Etkinlik bitişi",
            ["version"] = "Sürüm", ["summary"] = "Özet", ["run"] = "Çalışma",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Draft"] = "Taslak", ["Active"] = "Açık", ["Inactive"] = "Kapalı" },
            },
        },
        new(AuditedEntityKeys.ApprovalDefinition, "Onay süreci", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["currency"] = "Para birimi", ["isActive"] = "Açık", ["revision"] = "Sürüm", ["steps"] = "Adımlar",
            ["target"] = "Kayıt türü", ["gatedCommands"] = "Onaysız yapılamaz",
        }, "name"),
        new(AuditedEntityKeys.ApprovalRequest, "Onay talebi", null, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["process"] = "Süreç", ["amount"] = "Tutar", ["currency"] = "Para birimi", ["status"] = "Durum",
            ["step"] = "Adım", ["decision"] = "Karar", ["comment"] = "Not", ["approver"] = "Onaycı", ["record"] = "Kayıt", ["reason"] = "Neden", ["onBehalfOf"] = "Adına karar verilen", ["escalated"] = "Süre aşımı",
        }, "title")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Pending"] = "Bekliyor", ["Approved"] = "Onaylandı", ["Rejected"] = "Reddedildi", ["Withdrawn"] = "Geri çekildi", ["Invalidated"] = "Geçersizleşti" },
                ["decision"] = new Dictionary<string, string> { ["Approved"] = "Onay", ["Rejected"] = "Ret" },
            },
        },
        new(AuditedEntityKeys.ApprovalDelegation, "Onay vekâleti", null, new Dictionary<string, string>
        {
            ["principal"] = "Vekâlet veren", ["delegate"] = "Vekil", ["startsAt"] = "Başlangıç", ["endsAt"] = "Bitiş", ["reason"] = "Gerekçe", ["revokedAt"] = "Geri alındı",
        }, "delegate"),
        new(AuditedEntityKeys.Report, "Rapor", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["dataset"] = "Veri kümesi", ["visibility"] = "Paylaşım", ["sharedWith"] = "Paylaşılan kullanıcılar",
            ["summary"] = "Özet",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["visibility"] = new Dictionary<string, string> { ["Private"] = "Özel", ["Users"] = "Seçilen kullanıcılar", ["Tenant"] = "Firma" },
            },
        },
        new(AuditedEntityKeys.ReportSchedule, "Zamanlanmış rapor", null, new Dictionary<string, string>
        {
            ["report"] = "Rapor", ["frequency"] = "Sıklık", ["hour"] = "Saat", ["dayOfWeek"] = "Haftanın günü", ["dayOfMonth"] = "Ayın günü",
            ["enabled"] = "Açık", ["stoppedReason"] = "Durma nedeni",
        }, "report")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["frequency"] = new Dictionary<string, string> { ["Daily"] = "Günlük", ["Weekly"] = "Haftalık", ["Monthly"] = "Aylık" },
            },
        },
        new(AuditedEntityKeys.Membership, "Kullanıcı", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["displayName"] = "Ad soyad", ["status"] = "Durum", ["roles"] = "Roller",
            ["companies"] = "Şirketler", ["businessUnit"] = "Departman",
            ["businessUnitChangeReason"] = "Departman değişikliği gerekçesi", ["businessUnitRecords"] = "Departman değişikliğinde kayıtlar",
            ["invitedAt"] = "Davet", ["lockedOut"] = "Kilit", ["passwordResetRequestedAt"] = "Şifre sıfırlama bağlantısı",
            ["sessionsClosedAt"] = "Oturumları kapatıldı",
        }, "displayName")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Active"] = "Etkin", ["Disabled"] = "Devre dışı", ["Invited"] = "Davet bekliyor" },
            },
        },
        new(AuditedEntityKeys.Tenant, "Firma bilgileri", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["slug"] = "Kısa ad", ["status"] = "Durum", ["modules"] = "Açık modüller", ["roles"] = "Hazır roller", ["logo"] = "Logo", ["profile"] = "Sektör profili", ["scopeLimits"] = "Kayıt görünürlüğü",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Active"] = "Açık", ["Closed"] = "Kapalı" },
            },
        },
        new(AuditedEntityKeys.ActivitySelection, "Faaliyet seçimi", null, new Dictionary<string, string>
        {
            ["activities"] = "Seçili faaliyetler",
        }, "activities"),
        new(AuditedEntityKeys.RuleAdaptation, "Alan kuralı uyarlaması", null, new Dictionary<string, string>
        {
            ["rules"] = "Firmanın kuralları", ["disabled"] = "Kapatılan kurallar",
        }, "rules"),
        new(AuditedEntityKeys.SubProfile, "Alt profil", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["code"] = "Kod", ["description"] = "Açıklama", ["activity"] = "Bağlı faaliyet", ["status"] = "Durum", ["options"] = "Seçenekler", ["features"] = "Hizmet özellikleri",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Draft"] = "Taslak", ["Active"] = "Etkin", ["Inactive"] = "Pasif" },
            },
        },
        new(AuditedEntityKeys.TenantSetting, "Firma ayarı", null, new Dictionary<string, string>
        {
            ["key"] = "Ayar", ["value"] = "Değer",
        }, "key"),
        // Ortak kayıtlar (ADR-0006): değerleri ortak kaydı kullanan her modülün okuruna açık olduğu için denetim okuruna da açıktır.
        new(SharedEntities.Party, "Firma (ortak kayıt)", null, new Dictionary<string, string>
        {
            ["kind"] = "Tür", ["name"] = "Ünvan", ["taxOffice"] = "Vergi dairesi", ["taxNumber"] = "Vergi no", ["phone"] = "Telefon",
            ["email"] = "E-posta", ["website"] = "Web sitesi", ["city"] = "Şehir", ["address"] = "Adres",
            ["relationshipRoles"] = "İlişki rolleri", ["customerSectors"] = "Müşteri sektörü", ["serviceNeeds"] = "Hizmet ihtiyacı",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["kind"] = new Dictionary<string, string> { ["Organization"] = "Tüzel kişi", ["Individual"] = "Gerçek kişi" },
            },
        },
        new(SharedEntities.Person, "Kişi (ortak kayıt)", null, new Dictionary<string, string>
        {
            ["firstName"] = "Ad", ["lastName"] = "Soyad", ["email"] = "E-posta", ["phone"] = "Telefon", ["title"] = "Unvan",
            ["partyId"] = "Firma",
        })
        {
            TitleFields = ["firstName", "lastName"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party },
        },
        new(SharedEntities.PartyRelationship, "Firma ilişkisi (ortak kayıt)", null, new Dictionary<string, string>
        {
            ["partyId"] = "Firma", ["roleCode"] = "İlişki rolü", ["status"] = "Durum", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş", ["notes"] = "Not",
        }, "roleCode")
        {
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party },
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string>
                {
                    ["Prospect"] = "Aday", ["Active"] = "Aktif", ["Suspended"] = "Askıda", ["Ended"] = "Sona erdi",
                },
            },
        },
        new(AuditedEntityKeys.Audit, "Denetim kaydı", null, new Dictionary<string, string>
        {
            ["filters"] = "Filtreler", ["rows"] = "Satır sayısı",
        }),
        new(AuditedEntityKeys.EventDelivery, "Olay teslimi", null, new Dictionary<string, string>
        {
            ["status"] = "Durum", ["attemptCount"] = "Deneme sayısı", ["subscription"] = "Abonelik", ["eventId"] = "Olay",
            ["eventType"] = "Olay türü", ["entityKey"] = "Kayıt türü", ["recordId"] = "Kayıt",
        }, "subscription")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string>
                {
                    ["Pending"] = "Bekliyor", ["Succeeded"] = "Başarılı", ["Failed"] = "Başarısız", ["Skipped"] = "Atlandı",
                },
            },
        },
        new(AuditedEntityKeys.Account, "Hesap", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["displayName"] = "Ad soyad", ["ip"] = "IP adresi", ["userAgent"] = "Tarayıcı",
            ["reason"] = "Neden", ["sessions"] = "Kapatılan oturum", ["recoveryCodesLeft"] = "Kalan kurtarma kodu",
            ["language"] = "Arayüz dili",
        }, "email")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["reason"] = new Dictionary<string, string>
                {
                    [AccountAuditReasons.WrongPassword] = "Hatalı şifre",
                    [AccountAuditReasons.Locked] = "Hesap kilitliyken deneme",
                    [AccountAuditReasons.WrongTwoFactorCode] = "Hatalı doğrulama kodu",
                },
            },
        },
    ];

    /// <summary>
    /// Atama e-postası: sırası önemsiz, izin bildirmez (alıcının yetkisi gönderim anında, kendi kimliğiyle denetlenir).
    /// Kayıt türleri boş bırakılır; katalog modüllerin sorumlu atanan kayıt türleriyle doldurur.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        EventSubscription.For<AssignmentEmailSubscriber>(
            Subscriptions.AssignmentEmail, [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated], [], []) with { Ordered = false },
        EventSubscription.For<ApprovalRecordSubscriber>(
            Subscriptions.Approvals, [DomainEventTypes.RecordUpdated], [], []) with { Ordered = false },
        EventSubscription.For<AutomationEventSubscriber>(
            Subscriptions.Automation, [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated], [], []) with { Ordered = false },
    ];

    /// <summary>
    /// Core'un kural yeteneği: sektörden bağımsız aksiyonlar, "Onaya gönder" (Dilim 4c-2) ile kalıcı akışın "Bekle" ve "Onay sonucunu bekle"
    /// adımları (Dilim 4c-4), "Bildirim gönder" ve süreli belge kayıt türü (ADR-0038 dilim 2b: bitişe kala tetikleyicisi).
    /// </summary>
    Modulix.Core.Contracts.Automation.AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities = Modulix.Core.Credentials.CredentialAutomation.Capabilities.Entities,
        Events = Modulix.Core.Credentials.CredentialAutomation.Capabilities.Events,
        Fields = Modulix.Core.Credentials.CredentialAutomation.Capabilities.Fields,
        Actions = [.. Modulix.Core.Approvals.ApprovalAutomation.Capabilities.Actions, .. FlowAutomation.Actions, .. NotifyAutomation.Actions],
    };

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<AssignmentEmailSubscriber>();
        services.AddScoped<AutomationEventSubscriber>();
        services.AddScoped<ApprovalRecordSubscriber>();

        // "Onaya gönder" aksiyonu ve yayındaki süreç adı denetimi (Dilim 4c-2).
        services.AddScoped<Modulix.Core.Approvals.RequestApprovalAction>();
        services.AddScoped<IAutomationStepCheck, Modulix.Core.Approvals.ApprovalProcessCheck>();

        // Kalıcı akışın bekleme adımları ve yayın denetimleri (Dilim 4c-4).
        services.AddScoped<FlowWaitAction>();
        services.AddScoped<FlowAwaitApprovalAction>();
        services.AddScoped<FlowStopAction>();
        services.AddScoped<IAutomationStepCheck, FlowWaitCheck>();
        services.AddScoped<IAutomationStepCheck, FlowAwaitApprovalCheck>();

        // Süreli belgenin tarama kaynağı ve "Bildirim gönder" aksiyonu (ADR-0038 dilim 2b).
        services.AddScoped<Modulix.Core.Credentials.CredentialAutomationRecordSource>();
        services.AddScoped<NotifyAction>();
    }

    /// <summary>Kişinin çalıştığı firma: kişisi olan taraf silinemez.</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, SharedEntities.Person, "partyId", "kişi"),
    ];
}

/// <summary>Başarısız giriş nedenleri (<c>core.account</c> kaydının <c>reason</c> alanı).</summary>
public static class AccountAuditReasons
{
    public const string WrongPassword = "WrongPassword";
    public const string Locked = "Locked";
    public const string WrongTwoFactorCode = "WrongTwoFactorCode";
}
