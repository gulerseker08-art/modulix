using Modulix.Core.Contracts.Modules;
using Modulix.Shared.Warehouse;

namespace Modulix.Modules.Warehouse.Tests;

/// <summary>
/// Arayüz kuralı (CLAUDE.md → Arayüz tasarım kuralları): her kayıt formu sekmelidir; varsayılan düzenler çok sekmelidir ve metadata'dan
/// çizilir (veritabanı gerektirmez). Ayrıca arayüzün kullandığı anahtarlar sunucudaki modülle aynı kalır.
/// </summary>
public class WarehouseFormDefaultsTests
{
    private static readonly IModule Module = new WarehouseModule();

    [Fact]
    public void Every_default_form_has_several_tabs_and_each_tab_holds_a_section()
    {
        Assert.NotEmpty(WarehouseForms.All);
        foreach (var form in WarehouseForms.All)
        {
            Assert.True(form.Tabs.Count >= 2, $"{form.EntityKey}: varsayılan form sekmeli olmalı (en az 2 sekme).");
            Assert.Equal("genel", form.Tabs[0].Key);
            Assert.Equal(form.Tabs.Count, form.Tabs.Select(t => t.Key).Distinct().Count());
            foreach (var tab in form.Tabs)
            {
                Assert.True(form.Sections.Any(s => s.Tab == tab.Key), $"{form.EntityKey}: '{tab.Key}' sekmesinde bölüm yok.");
            }

            Assert.Contains(form.Sections, s => s.Key == FormDefinition.ExtrasSection);
        }
    }

    [Fact]
    public void Required_and_fixed_identity_fields_stay_on_the_first_tab_and_spans_are_allowed()
    {
        foreach (var form in WarehouseForms.All)
        {
            var firstTabSections = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
            foreach (var field in form.Fields.Where(f => f is { Required: true, Fixed: true }))
            {
                Assert.True(firstTabSections.Contains(field.Section), $"{form.EntityKey}: zorunlu '{field.Key}' alanı ilk sekmede değil.");
            }

            Assert.All(form.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
            Assert.All(form.Fields, f => Assert.Contains(form.Sections, s => s.Key == f.Section));
        }
    }

    [Fact]
    public void Every_form_belongs_to_a_customizable_record_type_of_the_module()
    {
        var customizable = Module.CustomizableEntities.Select(c => c.EntityKey).ToHashSet();

        Assert.All(WarehouseForms.All, form => Assert.Contains(form.EntityKey, customizable));
    }

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        var permissions = Module.Permissions.Select(p => p.Key).ToHashSet();

        Assert.Equal(WarehouseModule.ModuleKey, WarehouseAccess.ModuleKey);
        Assert.All(
            new[]
            {
                WarehouseAccess.FacilitiesRead, WarehouseAccess.FacilitiesWrite, WarehouseAccess.ZonesRead, WarehouseAccess.ZonesWrite,
                WarehouseAccess.LocationLevelsRead, WarehouseAccess.LocationLevelsWrite, WarehouseAccess.LocationsRead, WarehouseAccess.LocationsWrite,
            },
            key => Assert.Contains(key, permissions));
        Assert.Equal(8, permissions.Count);
        Assert.Equal(
            [WarehouseModule.Entities.Facility, WarehouseModule.Entities.Zone, WarehouseModule.Entities.LocationLevel, WarehouseModule.Entities.Location],
            new[] { WarehouseAccess.FacilityEntityKey, WarehouseAccess.ZoneEntityKey, WarehouseAccess.LocationLevelEntityKey, WarehouseAccess.LocationEntityKey });
        Assert.Equal(WarehouseModule.Lists.ZoneKind, WarehouseAccess.ZoneKindList);
        Assert.Equal(WarehouseModule.ZoneKindCategories.Controlled, WarehouseAccess.ZoneKindControlled);
        Assert.Equal(WarehouseModule.ZoneKindCategories.Free, WarehouseAccess.ZoneKindFree);
    }
}
