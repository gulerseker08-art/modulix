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
    public void Vehicle_kinds_are_categorised_as_powered_or_towed_and_the_default_exists()
    {
        var list = Assert.Single(Module.ChoiceLists);

        Assert.Equal(FleetModule.Lists.VehicleKind, list.Key);
        Assert.Equal([FleetModule.VehicleKindCategories.Powered, FleetModule.VehicleKindCategories.Towed], list.Categories.Select(c => c.Key));
        Assert.All(list.Values, v => Assert.Contains(v.Category, list.Categories.Select(c => c.Key)));
        Assert.Contains(list.Values, v => v.Key == list.Default);
        Assert.Contains(list.Values, v => v.Category == FleetModule.VehicleKindCategories.Towed); // dorse ayrı tablo değil, araç türüdür
    }

    [Fact]
    public void The_default_vehicle_form_has_several_tabs_and_keeps_identity_on_the_first_tab()
    {
        var form = Assert.Single(FleetForms.All);

        Assert.True(form.Tabs.Count >= 2);
        Assert.Equal("genel", form.Tabs[0].Key);
        Assert.All(form.Tabs, tab => Assert.Contains(form.Sections, s => s.Tab == tab.Key));
        Assert.Contains(form.Sections, s => s.Key == FormDefinition.ExtrasSection);
        var firstTab = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
        Assert.All(form.Fields.Where(f => f is { Required: true, Fixed: true }), f => Assert.Contains(f.Section, firstTab));
        Assert.All(form.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
        Assert.Contains(form.EntityKey, Module.CustomizableEntities.Select(c => c.EntityKey));
    }

    [Fact]
    public void Carrier_lookup_is_limited_to_parties_with_the_carrier_role()
    {
        var lookup = Assert.Single(Module.Lookups);

        Assert.Equal(SharedEntities.Party, lookup.TargetEntityKey);
        Assert.Equal("relationshipRole", lookup.FixedFilterName);
        Assert.Equal(FleetModule.RelationshipRoles.Carrier, lookup.FixedFilterValue);
        Assert.Contains(Module.SharedRecordReferences, r => r.Field == "carrierPartyId");
    }

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        Assert.Equal(FleetModule.ModuleKey, FleetAccess.ModuleKey);
        Assert.Equal(FleetModule.Permissions.VehiclesRead, FleetAccess.VehiclesRead);
        Assert.Equal(FleetModule.Permissions.VehiclesWrite, FleetAccess.VehiclesWrite);
        Assert.Equal(FleetModule.Entities.Vehicle, FleetAccess.VehicleEntityKey);
        Assert.Equal(FleetModule.Lists.VehicleKind, FleetAccess.VehicleKindList);
        Assert.Equal(FleetLookupDefinitions.VehicleCarrier, FleetAccess.VehicleCarrierLookup);
        Assert.Equal(FleetModule.VehicleKindCategories.Towed, FleetAccess.KindTowed);
        Assert.Equal(Vehicle.Ownerships.All, FleetAccess.Ownerships.Select(o => o.Key));
        Assert.Equal(Vehicle.Statuses.All, FleetAccess.Statuses.Select(s => s.Key));
        Assert.Equal(Vehicle.Ownerships.Subcontracted, FleetAccess.Subcontracted);
    }
}
