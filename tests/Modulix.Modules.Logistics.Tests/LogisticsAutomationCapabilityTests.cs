using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>
/// ADR-0027: kural altyapısı başka bir modülle (CRM olmadan) aynı sözleşmeyle çalışır. Katalog yalnızca Core ve Lojistik ile
/// kurulur; CRM projesine başvuru gerekmez.
/// </summary>
public class LogisticsAutomationCapabilityTests
{
    private static ModuleCatalog Catalog() => ModuleCatalog.Create(new LogisticsModule());

    [Fact]
    public void Catalog_with_only_core_and_logistics_accepts_the_capability_declaration()
    {
        var catalog = Catalog();

        Assert.Equal(["core", "logistics"], catalog.Modules.Select(m => m.Key));
        Assert.Equal(
            [LogisticsModule.AutomationEntityKeys.Shipment, LogisticsModule.AutomationEntityKeys.TransportRequest],
            catalog.AutomationEntities.Where(e => e.ModuleKey != "core").Select(e => e.Entity.EntityKey).Order());
        Assert.All(catalog.AutomationEntities.Where(e => e.ModuleKey != "core"), e => Assert.Equal("logistics", e.ModuleKey));
    }

    [Fact]
    public void Shipment_status_dates_and_weight_are_typed_rule_fields_backed_by_a_scan_source()
    {
        var catalog = Catalog();

        // Durum seçenekleri sunucunun durum listesidir; tarih alanları "N gün kala" taramasına, ağırlık eşik kuralına açıktır (ADR-0038 dilim 4).
        var status = catalog.FindAutomationField("logistics.shipment.status")!;
        Assert.Equal(AutomationFieldType.Choice, status.Type);
        Assert.Equal(Domain.Shipment.Statuses.All, status.Options.Select(o => o.Key));
        Assert.Equal(AutomationFieldType.Date, catalog.FindAutomationField("logistics.shipment.pickupDate")!.Type);
        Assert.Equal(AutomationFieldType.Date, catalog.FindAutomationField("logistics.transport-request.deliveryBy")!.Type);
        Assert.Equal(AutomationFieldType.Number, catalog.FindAutomationField("logistics.shipment.weightKg")!.Type);
        Assert.Contains(AutomationOperator.Gte, AutomationOperators.For(AutomationFieldType.Number));
        Assert.All(catalog.AutomationEntities.Where(e => e.ModuleKey == "logistics"), e => Assert.NotNull(e.Entity.RecordSource));
    }

    [Fact]
    public void Declared_events_belong_to_logistics_entities_and_use_known_event_types()
    {
        var catalog = Catalog();

        var events = catalog.AutomationEntities.Where(e => e.ModuleKey == "logistics").SelectMany(e => catalog.AutomationEventsOf(e.Entity.EntityKey)).ToList();

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.StartsWith("logistics.", e.Key));
        Assert.Contains(events, e => e.EventType == DomainEventTypes.RecordCreated);
        Assert.Contains(events, e => e.EventType == DomainEventTypes.RecordUpdated);
    }

    [Fact]
    public void Entities_are_protected_by_the_modules_own_read_permissions()
    {
        var catalog = Catalog();

        Assert.Equal(LogisticsModule.Permissions.ShipmentsRead, catalog.FindAutomationEntity("logistics.shipment")!.Value.Entity.ReadPermission);
        Assert.Null(catalog.FindAutomationEntity("logistics.vehicle")); // araç Filo modülüne taşındı (ADR-0038 K1)
    }
}
