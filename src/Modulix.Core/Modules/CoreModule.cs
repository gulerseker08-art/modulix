using Modulix.Core.Contracts.Modules;

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
    }

    public string Key => ModuleKey;

    public string DisplayName => "Çekirdek";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ManageUsers, "Kullanıcıları ve kullanıcıların rollerini yönetir", "Kullanıcılar"),
        new(Permissions.ManageRoles, "Rolleri ve izinlerini yönetir", "Roller"),
        new(Permissions.ManageSettings, "Firma ayarlarını değiştirir", "Firma ayarları"),
        new(Permissions.ReadAudit, "Denetim kaydını görüntüler", "Denetim kaydı"),
    ];

    /// <summary>Denetim kayıt türleri (Y5). Yönetim kayıtlarının değerleri her denetim okuruna açıktır.</summary>
    public static class AuditedEntityKeys
    {
        public const string Role = "core.role";
        public const string Membership = "core.membership";
        public const string Tenant = "core.tenant";
        public const string TenantSetting = "core.tenant-setting";
        public const string Audit = "core.audit";

        /// <summary>Oturum olayları (giriş, hatalı şifre, kilitlenme, şifre sıfırlama, davet kabulü); kayıt kişinin hesabıdır.</summary>
        public const string Account = "core.account";
    }

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(AuditedEntityKeys.Role, "Rol", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["permissions"] = "İzinler", ["isSystem"] = "Sistem rolü",
        }, "name"),
        new(AuditedEntityKeys.Membership, "Kullanıcı", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["displayName"] = "Ad soyad", ["status"] = "Durum", ["roles"] = "Roller",
            ["invitedAt"] = "Davet", ["lockedOut"] = "Kilit", ["passwordResetRequestedAt"] = "Şifre sıfırlama bağlantısı",
        }, "displayName")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Active"] = "Etkin", ["Disabled"] = "Devre dışı", ["Invited"] = "Davet bekliyor" },
            },
        },
        new(AuditedEntityKeys.Tenant, "Firma bilgileri", null, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["slug"] = "Kısa ad", ["status"] = "Durum", ["modules"] = "Açık modüller", ["roles"] = "Hazır roller",
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
        new(AuditedEntityKeys.Account, "Oturum", null, new Dictionary<string, string>
        {
            ["email"] = "E-posta", ["ip"] = "IP adresi", ["userAgent"] = "Tarayıcı", ["reason"] = "Neden",
        }, "email")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["reason"] = new Dictionary<string, string>
                {
                    [AccountAuditReasons.WrongPassword] = "Hatalı şifre",
                    [AccountAuditReasons.Locked] = "Hesap kilitliyken deneme",
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
}
