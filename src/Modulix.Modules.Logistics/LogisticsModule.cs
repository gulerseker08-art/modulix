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
}
