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
        Assert.All(Module.AuditedEntities.SelectMany(a => a.References.Values), target => Assert.Contains(target, audited.Keys));
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
}
