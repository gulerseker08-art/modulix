using Modulix.Core.Contracts.Modules;
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

        /// <summary>Firma ayarlarını yazar; okumak için izin gerekmez.</summary>
        public const string ManageSettings = "core.settings.manage";

        public const string ReadAudit = "core.audit.read";

        /// <summary>Özelleştirme (Y9): özel alanlar, listeler ve sektör profili.</summary>
        public const string ManageCustomization = "core.customization.manage";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Çekirdek";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ManageUsers, "Kullanıcıları ve kullanıcıların rollerini yönetir", "Kullanıcılar"),
        new(Permissions.ManageRoles, "Rolleri ve izinlerini yönetir", "Roller"),
        new(Permissions.ManageSettings, "Firma ayarlarını değiştirir", "Firma ayarları"),
        new(Permissions.ReadAudit, "Denetim kaydını görüntüler", "Denetim kaydı"),
        new(Permissions.ManageCustomization, "Özel alanları ve listeleri düzenler", "Özelleştirme"),
    ];

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
        public const string Membership = "core.membership";
        public const string Tenant = "core.tenant";
        public const string TenantSetting = "core.tenant-setting";
        public const string Audit = "core.audit";

        /// <summary>Oturum olayları (giriş, hatalı şifre, kilitlenme, şifre sıfırlama, davet kabulü); kayıt kişinin hesabıdır.</summary>
        public const string Account = "core.account";
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
        public const string RequireTwoFactor = "core.security.require-two-factor";
    }

    /// <summary>Evet/Hayır ayarlarının değerleri.</summary>
    public static class SettingValues
    {
        public const string Yes = "true";
        public const string No = "false";
    }

    private const string ProfileGroup = "Firma profili";
    private const string RegionalGroup = "Bölgesel ayarlar";
    private const string SecurityGroup = "Güvenlik";

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

        // Y7b: açıkken iki adımlı doğrulaması kapalı biri bu firmanın ekranlarına giremez, önce kurulumu yapar.
        new(SettingKeys.RequireTwoFactor, "İki adımlı doğrulama zorunlu", SecurityGroup, SettingType.Choice, SettingValues.No)
        {
            Description = "Açıksa iki adımlı doğrulaması kapalı kullanıcılar firmaya girmeden önce doğrulama uygulamasını kurar. "
                + "Açabilmek için sizin iki adımlı doğrulamanız açık olmalı.",
            Options = [new(SettingValues.No, "Hayır"), new(SettingValues.Yes, "Evet")],
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(AuditedEntityKeys.Role, "Rol", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["permissions"] = "İzinler", ["isSystem"] = "Sistem rolü", ["scope"] = "Kayıt kapsamı",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["scope"] = new Dictionary<string, string>
                {
                    ["All"] = "Tüm kayıtlar", ["Team"] = "Ekibinin kayıtları", ["Own"] = "Yalnızca kendi kayıtları",
                },
            },
        },
        new(AuditedEntityKeys.Team, "Ekip", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["members"] = "Üyeler",
        }, "name"),
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
        new(AuditedEntityKeys.Membership, "Kullanıcı", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["displayName"] = "Ad soyad", ["status"] = "Durum", ["roles"] = "Roller",
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
            ["name"] = "Ad", ["slug"] = "Kısa ad", ["status"] = "Durum", ["modules"] = "Açık modüller", ["roles"] = "Hazır roller", ["logo"] = "Logo", ["profile"] = "Sektör profili",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Active"] = "Açık", ["Closed"] = "Kapalı" },
            },
        },
        new(AuditedEntityKeys.TenantSetting, "Firma ayarı", null, new Dictionary<string, string>
        {
            ["key"] = "Ayar", ["value"] = "Değer",
        }, "key"),
        new(AuditedEntityKeys.Audit, "Denetim kaydı", null, new Dictionary<string, string>
        {
            ["filters"] = "Filtreler", ["rows"] = "Satır sayısı",
        }),
        new(AuditedEntityKeys.Account, "Hesap", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["displayName"] = "Ad soyad", ["ip"] = "IP adresi", ["userAgent"] = "Tarayıcı",
            ["reason"] = "Neden", ["sessions"] = "Kapatılan oturum", ["recoveryCodesLeft"] = "Kalan kurtarma kodu",
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
}

/// <summary>Başarısız giriş nedenleri (<c>core.account</c> kaydının <c>reason</c> alanı).</summary>
public static class AccountAuditReasons
{
    public const string WrongPassword = "WrongPassword";
    public const string Locked = "Locked";
    public const string WrongTwoFactorCode = "WrongTwoFactorCode";
}
