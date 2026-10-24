using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Modules;

public class ModuleCatalogTests
{
    [Fact]
    public void Catalog_lists_modules_sorted_by_key()
    {
        var catalog = new ModuleCatalog([new CoreModule(), Module("sales", "sales.orders.read"), Module("billing")]);

        Assert.Equal(["billing", "core", "sales"], catalog.Modules.Select(m => m.Key));
        Assert.True(catalog.ContainsPermission("sales.orders.read"));
        Assert.True(catalog.ContainsPermission(CoreModule.Permissions.ManageRoles));
        Assert.False(catalog.ContainsPermission("sales.orders.write"));
    }

    [Fact]
    public void Catalog_requires_core_module()
    {
        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog([Module("sales")]));
    }

    [Fact]
    public void Duplicate_module_key_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog([new CoreModule(), Module("sales"), Module("sales")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("Sales")]
    [InlineData("1sales")]
    [InlineData("sa-les")]
    [InlineData("sa.les")]
    public void Invalid_module_key_is_rejected(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog([new CoreModule(), Module(key)]));
    }

    [Theory]
    [InlineData("billing.orders.read")]
    [InlineData("sales.orders")]
    [InlineData("sales.Orders.read")]
    [InlineData("salesx.orders.read")]
    public void Permission_must_be_prefixed_with_its_module_and_well_formed(string permission)
    {
        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog([new CoreModule(), Module("sales", permission)]));
    }

    [Fact]
    public void Duplicate_permission_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ModuleCatalog([new CoreModule(), Module("sales", "sales.orders.read", "sales.orders.read")]));
    }

    [Fact]
    public void Core_is_always_enabled_other_modules_need_an_entitlement()
    {
        var catalog = new ModuleCatalog([new CoreModule(), Module("sales")]);
        var none = new HashSet<string>();

        Assert.True(catalog.IsEnabled("core", none));
        Assert.False(catalog.IsEnabled("sales", none));
        Assert.True(catalog.IsEnabled("sales", new HashSet<string> { "sales" }));
        Assert.False(catalog.IsEnabled("unknown", new HashSet<string> { "unknown" }));
    }

    [Fact]
    public void Effective_permissions_drop_disabled_modules_and_unknown_permissions()
    {
        var catalog = new ModuleCatalog(
        [
            new CoreModule(),
            Module("sales", "sales.orders.read"),
            Module("billing", "billing.invoices.read"),
        ]);

        var effective = catalog.EffectivePermissions(
            ["sales.orders.read", "billing.invoices.read", "sales.removed.read", CoreModule.Permissions.ManageRoles],
            new HashSet<string> { "sales" });

        Assert.Equal(
            [CoreModule.Permissions.ManageRoles, "sales.orders.read"],
            effective.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Registration_adds_core_and_rejects_invalid_catalog()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddModulixModules(Module("sales", "sales.orders.read"));

        var catalog = Assert.Single(services, d => d.ServiceType == typeof(ModuleCatalog)).ImplementationInstance as ModuleCatalog;
        Assert.NotNull(catalog);
        Assert.NotNull(catalog.Find("core"));
        Assert.NotNull(catalog.Find("sales"));
        Assert.Throws<InvalidOperationException>(() => services.AddModulixModules(Module("core")));
    }

    [Theory]
    [InlineData("crm.companies.read", "companies")]
    [InlineData("core.users.manage", "users")]
    [InlineData("sales.order-lines.items.write", "order-lines.items")]
    public void Resource_of_a_permission_is_the_part_between_module_and_action(string permission, string resource) =>
        Assert.Equal(resource, AccessKeys.ResourceOf(permission));

    [Fact]
    public void Templates_come_from_enabled_modules_and_read_only_covers_their_read_permissions()
    {
        var sales = new TestModule("sales", ["sales.orders.read", "sales.orders.write"],
            [new RoleTemplate("sales.clerk", "Satış", "", ["sales.orders.read", "sales.orders.write"])]);
        var stock = new TestModule("stock", ["stock.items.read"], [new RoleTemplate("stock.keeper", "Depo", "", ["stock.items.read"])]);
        var catalog = ModuleCatalog.Create(sales, stock);

        var templates = catalog.TemplatesFor(new HashSet<string> { "sales" });
        Assert.Equal(["sales.clerk", ModuleCatalog.ReadOnlyTemplateKey], templates.Select(t => t.Key));
        Assert.Equal(["sales.orders.read"], templates[^1].Permissions);

        Assert.Empty(catalog.TemplatesFor(new HashSet<string>()));
    }

    [Fact]
    public void Template_with_another_modules_permission_or_foreign_key_is_rejected()
    {
        var foreignPermission = new TestModule("sales", ["sales.orders.read"],
            [new RoleTemplate("sales.clerk", "Satış", "", [CoreModule.Permissions.ManageUsers])]);
        var foreignKey = new TestModule("sales", ["sales.orders.read"],
            [new RoleTemplate("stock.clerk", "Satış", "", ["sales.orders.read"])]);

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(foreignPermission));
        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(foreignKey));
    }

    [Fact]
    public void Sensitive_fields_are_hidden_without_their_permission()
    {
        var sales = new TestModule("sales", ["sales.orders.read", "sales.order-prices.read"])
        {
            SensitiveFields = [new SensitiveFields("sales.order", "sales.order-prices.read", ["price", "discount"])],
        };
        var catalog = ModuleCatalog.Create(sales);

        Assert.Equal(["discount", "price"], catalog.HiddenFields("sales.order", new HashSet<string> { "sales.orders.read" }).Order());
        Assert.Empty(catalog.HiddenFields("sales.order", new HashSet<string> { "sales.order-prices.read" }));
        Assert.Empty(catalog.HiddenFields("sales.customer", new HashSet<string>()));
    }

    [Fact]
    public void Sensitive_field_with_another_modules_permission_is_rejected()
    {
        var sales = new TestModule("sales", ["sales.orders.read"])
        {
            SensitiveFields = [new SensitiveFields("sales.order", CoreModule.Permissions.ManageUsers, ["price"])],
        };

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(sales));
    }

    private static IModule Module(string key, params string[] permissions) => new TestModule(key, permissions);

    private sealed class TestModule(string key, string[] permissions, IReadOnlyList<RoleTemplate>? templates = null) : IModule
    {
        public string Key => key;

        public string DisplayName => key;

        public IReadOnlyList<PermissionDefinition> Permissions { get; } =
            permissions.Select(p => new PermissionDefinition(p, p)).ToList();

        public IReadOnlyList<RoleTemplate> RoleTemplates { get; } = templates ?? [];

        public IReadOnlyList<SensitiveFields> SensitiveFields { get; init; } = [];
    }
}
