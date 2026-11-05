using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Records;

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

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Facility = "warehouse.facility";
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

    /// <summary>Denetim kaydında görünen kayıt türleri (Y5): tesis değişiklikleri kim, ne zaman, neyi değiştirdi olarak izlenir.</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Facility, "Tesis", Permissions.FacilitiesRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama", ["countryCode"] = "Ülke",
            ["customsEnabled"] = "Gümrük uzantısı", ["isActive"] = "Etkin",
        }, "name"),
    ];

    /// <summary>Firmanın özel alan ekleyebildiği kayıt türleri (Y9a).</summary>
    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Facility, "Tesis"),
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

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<WarehouseStoreContext>();
        services.AddScoped<IRecordStore, FacilityRecordStore>();
    }
}
