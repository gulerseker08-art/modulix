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
            [LogisticsModule.AutomationEntityKeys.Shipment],
            catalog.AutomationEntities.Select(e => e.Entity.EntityKey).Order());
        Assert.All(catalog.AutomationEntities, e => Assert.Equal("logistics", e.ModuleKey));
    }

    [Fact]
    public void Missing_document_and_capacity_threshold_can_be_expressed_with_typed_fields()
    {
        var catalog = Catalog();

        Assert.Equal(AutomationFieldType.Choice, catalog.FindAutomationField("logistics.shipment.documentStatus")!.Type);
        Assert.Equal(AutomationFieldType.Number, catalog.FindAutomationField("logistics.shipment.capacityUsagePercent")!.Type);
        Assert.Contains(AutomationOperator.Gte, AutomationOperators.For(AutomationFieldType.Number));
    }

    [Fact]
    public void Declared_events_belong_to_logistics_entities_and_use_known_event_types()
    {
        var catalog = Catalog();

        var events = catalog.AutomationEntities.SelectMany(e => catalog.AutomationEventsOf(e.Entity.EntityKey)).ToList();

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
