using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Modules;
using Modulix.Modules.Logistics.Domain;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>
/// ADR-0038 dilim 3: sürücü kartı beyanları (veritabanı gerektirmez). Sürücü kişinin uzantısıdır; Filo'nun aracına proje başvurusu
/// olmadan, kayıt türü anahtarı ve okuma sözleşmesiyle bağlanır. Arayüzün kullandığı anahtarlar sunucuyla aynıdır.
/// </summary>
public class LogisticsDriverModuleTests
{
    private static readonly IModule Module = new LogisticsModule();

    [Fact]
    public void The_driver_form_is_tabbed_and_keeps_the_person_on_the_first_tab()
    {
        var form = Assert.Single(LogisticsForms.All, f => f.EntityKey == LogisticsModule.Entities.Driver);

        Assert.Equal(LogisticsModule.Entities.Driver, form.EntityKey);
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
    public void Driver_is_a_person_extension_and_links_the_fleet_vehicle_only_by_key()
    {
        Assert.Contains(Module.SharedRecordAccess, a => a.EntityKey == SharedEntities.Person && a.WritePermission == LogisticsModule.Permissions.DriversWrite);
        Assert.Contains(Module.SharedRecordReferences, r => r.SharedEntityKey == SharedEntities.Person && r.Field == "personId");

        var vehicle = Assert.Single(Module.Lookups, l => l.Key == LogisticsLookupDefinitions.DriverVehicle);
        Assert.Equal("fleet.vehicle", vehicle.TargetEntityKey);
        // Taşıyıcı seçen her lookup "Taşıyıcı" rolüyle süzülür (sürücü, taşıyıcı anlaşması, taşeron sefer).
        var carriers = Module.Lookups.Where(l => l.FixedFilterName is not null).ToList();
        Assert.Equal([LogisticsLookupDefinitions.DriverCarrier, LogisticsLookupDefinitions.TermsCarrier, LogisticsLookupDefinitions.TripCarrier], carriers.Select(l => l.Key));
        Assert.All(carriers, l => Assert.Equal(LogisticsModule.RelationshipRoles.Carrier, l.FixedFilterValue));

        // Modül yalnızca Core.Contracts'a başvurur: Filo'ya derleme zamanı bağı yoktur.
        Assert.DoesNotContain(typeof(LogisticsModule).Assembly.GetReferencedAssemblies(), a => a.Name == "Modulix.Modules.Fleet");
    }

    [Fact]
    public void Operations_role_includes_driver_permissions_and_catalog_registers_with_core_only()
    {
        var catalog = ModuleCatalog.Create(new LogisticsModule());

        Assert.Equal(["core", "logistics"], catalog.Modules.Select(m => m.Key));
        var operations = Module.RoleTemplates.Single(t => t.Key == "logistics.operations");
        Assert.Contains(LogisticsModule.Permissions.DriversRead, operations.Permissions);
        Assert.Contains(LogisticsModule.Permissions.DriversWrite, operations.Permissions);
    }

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        Assert.Equal(LogisticsModule.ModuleKey, LogisticsAccess.ModuleKey);
        Assert.Equal(LogisticsModule.Permissions.DriversRead, LogisticsAccess.DriversRead);
        Assert.Equal(LogisticsModule.Permissions.DriversWrite, LogisticsAccess.DriversWrite);
        Assert.Equal(LogisticsModule.Entities.Driver, LogisticsAccess.DriverEntityKey);
        Assert.Equal(LogisticsLookupDefinitions.DriverPerson, LogisticsAccess.DriverPersonLookup);
        Assert.Equal(LogisticsLookupDefinitions.DriverCarrier, LogisticsAccess.DriverCarrierLookup);
        Assert.Equal(LogisticsLookupDefinitions.DriverVehicle, LogisticsAccess.DriverVehicleLookup);
        Assert.Equal(Driver.Types.All, LogisticsAccess.DriverTypes.Select(t => t.Key));
        Assert.Equal(Driver.Statuses.All, LogisticsAccess.Statuses.Select(s => s.Key));
        Assert.Equal(Driver.Types.Subcontracted, LogisticsAccess.Subcontracted);
    }
}
