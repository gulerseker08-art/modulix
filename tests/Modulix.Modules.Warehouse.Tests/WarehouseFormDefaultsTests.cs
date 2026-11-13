using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Warehouse.Domain;
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
    public void Lookups_point_at_known_record_types_and_dependent_ones_filter_by_facility()
    {
        var entities = new[]
        {
            WarehouseModule.Entities.Facility, WarehouseModule.Entities.Zone, WarehouseModule.Entities.LocationLevel, WarehouseModule.Entities.Location,
            WarehouseModule.Entities.PowerOfAttorney, WarehouseModule.Entities.StockItem, WarehouseModule.Entities.CustomsDeclaration,
            WarehouseModule.Entities.CustomsDeclarationLine, WarehouseModule.Entities.Lot, WarehouseModule.Entities.StockMovement,
        };

        Assert.All(Module.Lookups, lookup =>
        {
            Assert.Contains(lookup.SourceEntityKey, entities);
            Assert.True(
                entities.Contains(lookup.TargetEntityKey!) || lookup.TargetEntityKey == Modulix.Core.Contracts.SharedRecords.SharedEntities.Party,
                $"Bilinmeyen hedef: {lookup.TargetEntityKey}");
            Assert.StartsWith(WarehouseModule.ModuleKey + ".", lookup.Key, StringComparison.Ordinal);
        });
        // Sabit süzgeç ya ikisiyle tanımlıdır ya hiç; yalnızca firma lookup'larında ve ilişki rolüyle kullanılır.
        foreach (var fixedFilter in Module.Lookups.Where(l => l.FixedFilterName is not null))
        {
            Assert.Equal("relationshipRole", fixedFilter.FixedFilterName);
            Assert.False(string.IsNullOrWhiteSpace(fixedFilter.FixedFilterValue));
            Assert.Equal(Modulix.Core.Contracts.SharedRecords.SharedEntities.Party, fixedFilter.TargetEntityKey);
        }

        foreach (var dependent in Module.Lookups.Where(l => l.DependsOnField is not null))
        {
            Assert.Equal("facilityId", dependent.DependsOnField);
            Assert.Equal("facilityId", dependent.DependsOnFilter);
        }
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
                WarehouseAccess.AttorneysRead, WarehouseAccess.AttorneysWrite,
                WarehouseAccess.StockItemsRead, WarehouseAccess.StockItemsWrite,
                WarehouseAccess.DeclarationsRead, WarehouseAccess.DeclarationsWrite,
                WarehouseAccess.RegimesRead, WarehouseAccess.RegimesWrite,
                WarehouseAccess.LotsRead, WarehouseAccess.LotsWrite, WarehouseAccess.MovementsRead, WarehouseAccess.MovementsWrite,
            },
            key => Assert.Contains(key, permissions));
        Assert.Equal(20, permissions.Count);
        Assert.Equal(WarehouseModule.Entities.Lot, WarehouseAccess.LotEntityKey);
        Assert.Equal(WarehouseModule.Entities.StockMovement, WarehouseAccess.StockMovementEntityKey);
        Assert.Equal(WarehouseModule.Entities.StockBalance, WarehouseAccess.StockBalanceEntityKey);
        Assert.Equal(WarehouseModule.Lists.MovementType, WarehouseAccess.MovementTypeList);
        Assert.Equal(WarehouseModule.Lists.MovementReason, WarehouseAccess.MovementReasonList);
        Assert.Equal(
            [MovementCategories.Inbound, MovementCategories.Outbound, MovementCategories.Transfer, MovementCategories.Adjustment],
            new[] { WarehouseAccess.MovementInbound, WarehouseAccess.MovementOutbound, WarehouseAccess.MovementTransfer, WarehouseAccess.MovementAdjustment });
        Assert.Equal(WarehouseModule.Entities.RegimeBinding, WarehouseAccess.RegimeBindingEntityKey);
        Assert.Equal(
            [
                WarehouseModule.Entities.Facility, WarehouseModule.Entities.Zone, WarehouseModule.Entities.LocationLevel, WarehouseModule.Entities.Location,
                WarehouseModule.Entities.PowerOfAttorney, WarehouseModule.Entities.StockItem,
                WarehouseModule.Entities.CustomsDeclaration, WarehouseModule.Entities.CustomsDeclarationLine,
            ],
            new[]
            {
                WarehouseAccess.FacilityEntityKey, WarehouseAccess.ZoneEntityKey, WarehouseAccess.LocationLevelEntityKey, WarehouseAccess.LocationEntityKey,
                WarehouseAccess.PowerOfAttorneyEntityKey, WarehouseAccess.StockItemEntityKey,
                WarehouseAccess.CustomsDeclarationEntityKey, WarehouseAccess.CustomsDeclarationLineEntityKey,
            });
        Assert.Equal(
            Module.Lookups.Select(l => l.Key).Order(),
            new[]
            {
                WarehouseAccess.ZoneFacilityLookup, WarehouseAccess.LocationLevelFacilityLookup, WarehouseAccess.LocationFacilityLookup,
                WarehouseAccess.LocationParentLookup, WarehouseAccess.LocationZoneLookup,
                WarehouseAccess.AttorneyImporterLookup, WarehouseAccess.AttorneyBrokerLookup,
                WarehouseAccess.DeclarationImporterLookup, WarehouseAccess.DeclarationBrokerLookup,
                WarehouseAccess.LotStockItemLookup, WarehouseAccess.LotOwnerLookup, WarehouseAccess.LotBillToLookup,
                WarehouseAccess.MovementLotLookup, WarehouseAccess.MovementFromLocationLookup, WarehouseAccess.MovementToLocationLookup,
            }.Order());
        Assert.Equal(WarehouseModule.Lists.ZoneKind, WarehouseAccess.ZoneKindList);
        Assert.Equal(WarehouseModule.Lists.StockUnit, WarehouseAccess.StockUnitList);
        Assert.Equal(WarehouseModule.Lists.CustomsRegime, WarehouseAccess.CustomsRegimeList);
        Assert.Equal(WarehouseModule.Lists.DeclarationStatus, WarehouseAccess.DeclarationStatusList);
        Assert.Equal(WarehouseModule.Lists.ContainerType, WarehouseAccess.ContainerTypeList);
        Assert.Equal(WarehouseModule.Lists.OriginCountry, WarehouseAccess.OriginCountryList);
        Assert.Equal(WarehouseModule.Lists.DeclarationLineUnit, WarehouseAccess.DeclarationLineUnitList);
        Assert.Equal(WarehouseModule.DeclarationStatusCategories.Draft, WarehouseAccess.DeclarationDraft);
        Assert.Equal(WarehouseModule.DeclarationStatusCategories.Registered, WarehouseAccess.DeclarationRegistered);
        Assert.Equal(WarehouseModule.DeclarationStatusCategories.Cancelled, WarehouseAccess.DeclarationCancelled);
        Assert.Equal(WarehouseModule.ZoneKindCategories.Controlled, WarehouseAccess.ZoneKindControlled);
        Assert.Equal(WarehouseModule.ZoneKindCategories.Free, WarehouseAccess.ZoneKindFree);
    }
}
