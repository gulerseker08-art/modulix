using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Modules;
using Modulix.Modules.Fleet.Domain;
using Modulix.Shared.Fleet;

namespace Modulix.Modules.Fleet.Tests;

/// <summary>
/// ADR-0038 dilim 1a: Filo modülü Core ve başka modül olmadan, yalnızca sözleşmeyle kaydolur; varsayılan form sekmelidir; arayüzün
/// kullandığı anahtarlar sunucudaki modülle aynıdır (veritabanı gerektirmez).
/// </summary>
public class FleetModuleTests
{
    private static readonly IModule Module = new FleetModule();

    [Fact]
    public void Catalog_with_only_core_and_fleet_registers_the_module_and_its_vehicle_rules()
    {
        var catalog = ModuleCatalog.Create(new FleetModule());

        Assert.Equal(["core", "fleet"], catalog.Modules.Select(m => m.Key));
        var vehicle = catalog.FindAutomationEntity(FleetModule.Entities.Vehicle)!.Value;
        Assert.Equal(FleetModule.ModuleKey, vehicle.ModuleKey);
        Assert.Equal(FleetModule.Permissions.VehiclesRead, vehicle.Entity.ReadPermission);
        Assert.Equal(AutomationFieldType.Choice, catalog.FindAutomationField("fleet.vehicle.status")!.Type);
        Assert.Equal(
            Vehicle.Statuses.All,
            catalog.FindAutomationField("fleet.vehicle.status")!.Options.Select(o => o.Key));
    }

    [Fact]
    public void Permissions_and_role_templates_belong_to_the_module()
    {
        var own = Module.Permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(own, key => Assert.StartsWith(FleetModule.ModuleKey + ".", key, StringComparison.Ordinal));
        Assert.All(Module.RoleTemplates, template =>
        {
            Assert.StartsWith(FleetModule.ModuleKey + ".", template.Key, StringComparison.Ordinal);
            Assert.All(template.Permissions, permission => Assert.Contains(permission, own));
        });
    }

    [Fact]
    public void Vehicle_and_maintenance_kinds_are_categorised_and_their_defaults_exist()
    {
        var kinds = Assert.Single(Module.ChoiceLists, l => l.Key == FleetModule.Lists.VehicleKind);
        Assert.Equal([FleetModule.VehicleKindCategories.Powered, FleetModule.VehicleKindCategories.Towed], kinds.Categories.Select(c => c.Key));
        Assert.Contains(kinds.Values, v => v.Category == FleetModule.VehicleKindCategories.Towed); // dorse ayrı tablo değil, araç türüdür

        var maintenance = Assert.Single(Module.ChoiceLists, l => l.Key == FleetModule.Lists.MaintenanceKind);
        Assert.Equal(
            [FleetModule.MaintenanceKindCategories.Planned, FleetModule.MaintenanceKindCategories.Unplanned], maintenance.Categories.Select(c => c.Key));

        Assert.All(Module.ChoiceLists, list =>
        {
            Assert.All(list.Values, v => Assert.Contains(v.Category, list.Categories.Select(c => c.Key)));
            Assert.Contains(list.Values, v => v.Key == list.Default);
        });
    }

    [Fact]
    public void Default_forms_have_several_tabs_and_keep_identity_on_the_first_tab()
    {
        Assert.Equal([FleetModule.Entities.Vehicle, FleetModule.Entities.Maintenance], FleetForms.All.Select(f => f.EntityKey));
        foreach (var form in FleetForms.All)
        {
            Assert.True(form.Tabs.Count >= 2, form.EntityKey);
            Assert.Equal("genel", form.Tabs[0].Key);
            Assert.All(form.Tabs, tab => Assert.Contains(form.Sections, s => s.Tab == tab.Key));
            Assert.Contains(form.Sections, s => s.Key == FormDefinition.ExtrasSection);
            var firstTab = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
            Assert.All(form.Fields.Where(f => f is { Required: true, Fixed: true }), f => Assert.Contains(f.Section, firstTab));
            Assert.All(form.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
            Assert.Contains(form.EntityKey, Module.CustomizableEntities.Select(c => c.EntityKey));
        }
    }

    [Fact]
    public void Lookups_point_at_vehicles_or_parties_and_only_the_carrier_is_role_filtered()
    {
        Assert.All(Module.Lookups, lookup =>
        {
            Assert.StartsWith(FleetModule.ModuleKey + ".", lookup.Key, StringComparison.Ordinal);
            Assert.Contains(lookup.TargetEntityKey, new[] { SharedEntities.Party, FleetModule.Entities.Vehicle });
        });

        var carrier = Assert.Single(Module.Lookups, l => l.FixedFilterName is not null);
        Assert.Equal(FleetLookupDefinitions.VehicleCarrier, carrier.Key);
        Assert.Equal("relationshipRole", carrier.FixedFilterName);
        Assert.Equal(FleetModule.RelationshipRoles.Carrier, carrier.FixedFilterValue);
        Assert.Equal(["carrierPartyId", "supplierPartyId"], Module.SharedRecordReferences.Select(r => r.Field));
    }

    [Fact]
    public void Next_maintenance_date_is_a_date_rule_field_with_a_scan_source()
    {
        var catalog = ModuleCatalog.Create(new FleetModule());

        Assert.Equal(AutomationFieldType.Date, catalog.FindAutomationField("fleet.vehicle.nextMaintenanceDate")!.Type);
        Assert.NotNull(catalog.FindAutomationEntity(FleetModule.Entities.Vehicle)!.Value.Entity.RecordSource);
        Assert.Null(catalog.FindAutomationEntity(FleetModule.Entities.Maintenance)); // bakım kaydına kural yazılmaz; araç alanı yeter
    }

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        Assert.Equal(FleetModule.ModuleKey, FleetAccess.ModuleKey);
        Assert.Equal(FleetModule.Permissions.VehiclesRead, FleetAccess.VehiclesRead);
        Assert.Equal(FleetModule.Permissions.VehiclesWrite, FleetAccess.VehiclesWrite);
        Assert.Equal(FleetModule.Permissions.MaintenanceRead, FleetAccess.MaintenanceRead);
        Assert.Equal(FleetModule.Permissions.MaintenanceWrite, FleetAccess.MaintenanceWrite);
        Assert.Equal(FleetModule.Entities.Vehicle, FleetAccess.VehicleEntityKey);
        Assert.Equal(FleetModule.Entities.Maintenance, FleetAccess.MaintenanceEntityKey);
        Assert.Equal(FleetModule.Lists.VehicleKind, FleetAccess.VehicleKindList);
        Assert.Equal(FleetModule.Lists.MaintenanceKind, FleetAccess.MaintenanceKindList);
        Assert.Equal(FleetLookupDefinitions.VehicleCarrier, FleetAccess.VehicleCarrierLookup);
        Assert.Equal(FleetLookupDefinitions.MaintenanceVehicle, FleetAccess.MaintenanceVehicleLookup);
        Assert.Equal(FleetLookupDefinitions.MaintenanceSupplier, FleetAccess.MaintenanceSupplierLookup);
        Assert.Equal(FleetModule.VehicleKindCategories.Towed, FleetAccess.KindTowed);
        Assert.Equal(FleetModule.MaintenanceKindCategories.Unplanned, FleetAccess.MaintenanceUnplanned);
        Assert.Equal(Vehicle.Ownerships.All, FleetAccess.Ownerships.Select(o => o.Key));
        Assert.Equal(Vehicle.Statuses.All, FleetAccess.Statuses.Select(s => s.Key));
        Assert.Equal(Vehicle.Ownerships.Subcontracted, FleetAccess.Subcontracted);
    }
}
