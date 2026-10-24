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
        new(Permissions.ManageUsers, "Kullanıcıları ve kullanıcıların rollerini yönetir"),
        new(Permissions.ManageRoles, "Rolleri ve izinlerini yönetir"),
        new(Permissions.ManageSettings, "Firma ayarlarını değiştirir"),
        new(Permissions.ReadAudit, "Denetim kaydını görüntüler"),
    ];
}
