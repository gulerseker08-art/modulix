using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Logistics;

/// <summary>Lojistik: sevkiyat, taşıyıcı, maliyet.</summary>
public sealed class LogisticsModule : IModule
{
    public const string ModuleKey = "logistics";

    public static class Permissions
    {
        public const string ShipmentsRead = "logistics.shipments.read";
        public const string ShipmentsWrite = "logistics.shipments.write";
        public const string CarriersRead = "logistics.carriers.read";
        public const string CarriersWrite = "logistics.carriers.write";
        public const string CostsRead = "logistics.costs.read";
        public const string CostsWrite = "logistics.costs.write";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Lojistik";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ShipmentsRead, "Sevkiyatları görüntüler", "Sevkiyatlar"),
        new(Permissions.ShipmentsWrite, "Sevkiyat ekler ve düzenler", "Sevkiyatlar"),
        new(Permissions.CarriersRead, "Taşıyıcıları görüntüler", "Taşıyıcılar"),
        new(Permissions.CarriersWrite, "Taşıyıcı ekler ve düzenler", "Taşıyıcılar"),
        new(Permissions.CostsRead, "Maliyetleri görüntüler", "Maliyetler"),
        new(Permissions.CostsWrite, "Maliyet ekler ve düzenler", "Maliyetler"),
    ];

    /// <summary>Kural altyapısında kullanılan kayıt türleri (ADR-0027).</summary>
    public static class AutomationEntityKeys
    {
        public const string Shipment = "logistics.shipment";
        public const string Vehicle = "logistics.vehicle";
    }

    /// <summary>
    /// Kural altyapısı yetenekleri (ADR-0027): belge eksikliği, bakım tarihi ve kapasite eşiği kuralları CRM olmadan, aynı sözleşmeyle
    /// yazılabilir. Lojistik henüz iskelet modüldür (sevkiyat ve araç kayıt depoları yok); bu beyan yeteneğin Core'a modül koduna
    /// dokunmadan eklenebildiğini gösterir ve kayıt depoları gelince kural çalıştırmak ek Core değişikliği gerektirmez. Aksiyon
    /// beyan edilmez: lojistik aksiyonları kayıt servisleriyle birlikte gelir.
    /// </summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(AutomationEntityKeys.Shipment, "Sevkiyat", Permissions.ShipmentsRead),
            new(AutomationEntityKeys.Vehicle, "Araç", Permissions.CarriersRead),
        ],
        Events =
        [
            new("logistics.shipment.created", AutomationEntityKeys.Shipment, DomainEventTypes.RecordCreated, "Sevkiyat oluşturuldu"),
            new("logistics.shipment.updated", AutomationEntityKeys.Shipment, DomainEventTypes.RecordUpdated, "Sevkiyat güncellendi"),
            new("logistics.vehicle.updated", AutomationEntityKeys.Vehicle, DomainEventTypes.RecordUpdated, "Araç güncellendi"),
        ],
        Fields =
        [
            new(AutomationEntityKeys.Shipment, "documentStatus", "Belge durumu", AutomationFieldType.Choice)
            {
                Options = [new("complete", "Tam"), new("missing", "Eksik")],
                Hint = "Eksik belgesi olan sevkiyatı yakalamak için.",
            },
            new(AutomationEntityKeys.Shipment, "capacityUsagePercent", "Kapasite kullanımı (%)", AutomationFieldType.Number)
            {
                Hint = "Kapasite eşiği kuralları için.",
            },
            new(AutomationEntityKeys.Vehicle, "nextMaintenanceDate", "Sonraki bakım tarihi", AutomationFieldType.Date)
            {
                Hint = "Bakım tarihi yaklaşan araçlar için (tarihe kala tetikleyicisi).",
            },
        ],
    };

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "logistics.operations",
            "Operasyon",
            "Sevkiyatları ve taşıyıcıları görüntüler ve düzenler; maliyetleri görüntüler.",
            [
                Permissions.ShipmentsRead, Permissions.ShipmentsWrite,
                Permissions.CarriersRead, Permissions.CarriersWrite,
                Permissions.CostsRead,
            ]),
    ];
}
