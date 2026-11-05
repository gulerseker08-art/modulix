using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo: tesis, alan, lokasyon, stok kartı, parti, hareket defteri ve gümrük uzantısı (ADR-0032). Bu ilk adım yalnızca modülü
/// kaydeder (anahtar, izinler, rol önerisi); varlıklar, ayarlar, seçim listeleri ve formlar kullanıcılarıyla birlikte dilimlerle
/// gelir (tesis ve lokasyon: dilim 2). Gümrük uzantısı (<c>warehouse.customs.*</c> izinleri) dilim 4'ten itibaren eklenir.
/// </summary>
public sealed class WarehouseModule : IModule
{
    public const string ModuleKey = "warehouse";

    public static class Permissions
    {
        public const string FacilitiesRead = "warehouse.facilities.read";
        public const string FacilitiesWrite = "warehouse.facilities.write";
        public const string LocationsRead = "warehouse.locations.read";
        public const string LocationsWrite = "warehouse.locations.write";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Depo";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.FacilitiesRead, "Tesisleri görüntüler", "Tesisler"),
        new(Permissions.FacilitiesWrite, "Tesis ekler ve düzenler", "Tesisler"),
        new(Permissions.LocationsRead, "Lokasyonları görüntüler", "Lokasyonlar"),
        new(Permissions.LocationsWrite, "Lokasyon ekler ve düzenler", "Lokasyonlar"),
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "warehouse.manager",
            "Depo sorumlusu",
            "Tesisleri ve lokasyonları görüntüler ve düzenler.",
            [
                Permissions.FacilitiesRead, Permissions.FacilitiesWrite,
                Permissions.LocationsRead, Permissions.LocationsWrite,
            ]),
    ];
}
