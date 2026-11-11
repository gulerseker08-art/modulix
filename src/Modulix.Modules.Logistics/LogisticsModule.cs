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
    }

    /// <summary>
    /// Kural altyapısı yetenekleri (ADR-0027): belge eksikliği ve kapasite eşiği kuralları CRM olmadan, aynı sözleşmeyle
    /// yazılabilir. Lojistik henüz iskelet modüldür (sevkiyat kayıt deposu yok); bu beyan yeteneğin Core'a modül koduna
    /// dokunmadan eklenebildiğini gösterir ve kayıt depoları gelince kural çalıştırmak ek Core değişikliği gerektirmez. Aksiyon
    /// beyan edilmez: lojistik aksiyonları kayıt servisleriyle birlikte gelir. Araç beyanı Filo modülüne taşındı (`fleet.vehicle`, ADR-0038 K1).
    /// </summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(AutomationEntityKeys.Shipment, "Sevkiyat", Permissions.ShipmentsRead),
        ],
        Events =
        [
            new("logistics.shipment.created", AutomationEntityKeys.Shipment, DomainEventTypes.RecordCreated, "Sevkiyat oluşturuldu"),
            new("logistics.shipment.updated", AutomationEntityKeys.Shipment, DomainEventTypes.RecordUpdated, "Sevkiyat güncellendi"),
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
