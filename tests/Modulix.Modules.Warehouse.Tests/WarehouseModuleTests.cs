using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;

namespace Modulix.Modules.Warehouse.Tests;

/// <summary>ADR-0032: Depo modülü Core ve başka modül olmadan, yalnızca sözleşmeyle kaydolur.</summary>
public class WarehouseModuleTests
{
    private static readonly IModule Module = new WarehouseModule();

    [Fact]
    public void Catalog_with_only_core_and_warehouse_registers_the_module()
    {
        var catalog = ModuleCatalog.Create(new WarehouseModule());

        Assert.Equal(["core", "warehouse"], catalog.Modules.Select(m => m.Key));
    }

    [Fact]
    public void Every_permission_starts_with_the_module_key_and_is_unique()
    {
        var keys = Module.Permissions.Select(p => p.Key).ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.StartsWith(WarehouseModule.ModuleKey + ".", key, StringComparison.Ordinal));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Role_templates_use_only_the_module_own_permissions()
    {
        var own = Module.Permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(Module.RoleTemplates);
        Assert.All(Module.RoleTemplates, template =>
        {
            Assert.StartsWith(WarehouseModule.ModuleKey + ".", template.Key, StringComparison.Ordinal);
            Assert.All(template.Permissions, permission => Assert.Contains(permission, own));
        });
    }

    [Fact]
    public void Zone_kind_list_binds_to_the_zone_field_and_every_built_in_value_has_a_category_the_code_understands()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == WarehouseModule.Lists.ZoneKind);

        Assert.Equal((WarehouseModule.Entities.Zone, "kindKey"), (list.EntityKey, list.Field));
        Assert.Equal(
            [WarehouseModule.ZoneKindCategories.Controlled, WarehouseModule.ZoneKindCategories.Free],
            list.Categories.Select(c => c.Key).Order());
        Assert.All(list.Values, v => Assert.Contains(v.Category, list.Categories.Select(c => c.Key)));
        Assert.Contains(list.Default, list.Values.Select(v => v.Key));
    }

    [Fact]
    public void Audited_entities_cover_every_record_type_and_references_point_to_known_types()
    {
        var audited = Module.AuditedEntities.ToDictionary(a => a.EntityKey);

        Assert.Contains(WarehouseModule.Entities.Facility, audited.Keys);
        Assert.Contains(WarehouseModule.Entities.Zone, audited.Keys);
        Assert.Contains(WarehouseModule.Entities.PowerOfAttorney, audited.Keys);
        Assert.Contains(WarehouseModule.Entities.StockItem, audited.Keys);
        // Başvuru hedefi ya denetlenen bir depo kaydıdır ya da ortak kayıttır (firma, ADR-0006).
        Assert.All(
            Module.AuditedEntities.SelectMany(a => a.References.Values),
            target => Assert.True(audited.ContainsKey(target) || Modulix.Core.Contracts.SharedRecords.SharedEntities.All.Contains(target), target));
    }

    [Fact]
    public void Every_write_permission_has_a_matching_read_permission_so_a_writer_can_always_see_the_data()
    {
        var keys = Module.Permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var write in keys.Where(k => k.EndsWith(".write", StringComparison.Ordinal)))
        {
            Assert.Contains(write[..^".write".Length] + ".read", keys);
        }
    }

    [Fact]
    public void Power_of_attorney_is_valid_only_inside_its_dates_and_while_not_revoked()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 9, 1);
        bool Valid(bool active, DateOnly? end, DateOnly day) => Modulix.Modules.Warehouse.Domain.PowerOfAttorney.IsValid(active, from, end, day);

        Assert.False(Valid(true, to, new DateOnly(2026, 2, 28)));
        Assert.True(Valid(true, to, from));
        Assert.True(Valid(true, to, to));
        Assert.False(Valid(true, to, new DateOnly(2026, 9, 2)));

        // Bitiş boşsa süresizdir; iptal edilmiş vekâlet tarihi geçerli olsa da geçerli sayılmaz.
        Assert.True(Valid(true, null, new DateOnly(2040, 1, 1)));
        Assert.False(Valid(false, to, new DateOnly(2026, 6, 1)));
    }

    [Theory]
    [InlineData("8471.30.00.00.00", "8471300000" + "00")]
    [InlineData("0101 21 00", "01012100")]
    [InlineData("  090111  ", "090111")]
    [InlineData("12345678901234", "12345678901234")]
    public void Gtip_is_normalized_to_digits_and_keeps_leading_zeros(string input, string expected)
    {
        var (value, error) = Modulix.Modules.Warehouse.Domain.GtipRules.Normalize(input);

        Assert.Null(error);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("123456789012345")]
    [InlineData("8471A0")]
    [InlineData("8471-30")]
    public void Gtip_with_wrong_characters_or_length_is_rejected(string input)
    {
        var (value, error) = Modulix.Modules.Warehouse.Domain.GtipRules.Normalize(input);

        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_gtip_means_not_set_and_is_not_an_error(string? input)
    {
        Assert.Equal((null, null), Modulix.Modules.Warehouse.Domain.GtipRules.Normalize(input));
    }
}
