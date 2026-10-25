using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
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

    [Fact]
    public void Shared_record_access_collects_declared_permissions()
    {
        var sales = new TestModule("sales", ["sales.customers.read", "sales.customers.write"])
        {
            SharedRecordAccess = [new SharedRecordAccess(SharedEntities.Party, "sales.customers.read", "sales.customers.write")],
            SharedRecordReferences = [new SharedRecordReference(SharedEntities.Party, "sales.customer", "partyId", "müşteri")],
        };

        var catalog = ModuleCatalog.Create(sales);

        Assert.Equal("sales.customers.read", Assert.Single(catalog.SharedAccessTo(SharedEntities.Party)).ReadPermission);
        Assert.Empty(catalog.SharedAccessTo(SharedEntities.Person));
        Assert.Contains(catalog.SharedReferencesTo(SharedEntities.Party), r => r.EntityKey == "sales.customer");
        Assert.Contains(catalog.SharedReferencesTo(SharedEntities.Party), r => r.EntityKey == SharedEntities.Person);
    }

    [Theory]
    [InlineData("core.unknown", "sales.customers.read", null)]
    [InlineData(SharedEntities.Party, CoreModule.Permissions.ManageUsers, null)]
    [InlineData(SharedEntities.Party, "sales.customers.read", "sales.orders.write")]
    public void Shared_record_access_must_name_a_shared_record_and_own_permissions(string entityKey, string read, string? write)
    {
        var sales = new TestModule("sales", ["sales.customers.read"])
        {
            SharedRecordAccess = [new SharedRecordAccess(entityKey, read, write)],
        };

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(sales));
    }

    [Fact]
    public void Shared_record_reference_must_come_from_the_modules_own_record()
    {
        var sales = new TestModule("sales", [])
        {
            SharedRecordReferences = [new SharedRecordReference(SharedEntities.Party, "crm.company", "partyId", "firma")],
        };

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(sales));
    }

    [Fact]
    public void Event_subscriptions_are_found_by_event_and_record_type()
    {
        var sales = new TestModule("sales", ["sales.orders.read"])
        {
            AuditedEntities = [new AuditedEntity("sales.order", "Sipariş", null, new Dictionary<string, string>())],
            EventSubscriptions =
            [
                EventSubscription.For<NoopSubscriber>("sales.party-renamed", [DomainEventTypes.RecordUpdated], [SharedEntities.Party], ["sales.orders.read"]),
                EventSubscription.For<NoopSubscriber>("sales.any-change", [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated], [], []),
            ],
        };

        var catalog = ModuleCatalog.Create(sales);

        Assert.Equal(["sales.any-change", "sales.party-renamed"], catalog.SubscriptionsFor(DomainEventTypes.RecordUpdated, SharedEntities.Party).Select(s => s.Subscription.Key));
        Assert.Equal(["sales.any-change"], catalog.SubscriptionsFor(DomainEventTypes.RecordCreated, "sales.order").Select(s => s.Subscription.Key));
        Assert.Empty(catalog.SubscriptionsFor(DomainEventTypes.RecordDeleted, "sales.order"));
        Assert.Equal("sales", catalog.FindSubscription("sales.party-renamed")!.Value.ModuleKey);
        Assert.Null(catalog.FindSubscription("sales.yok"));
    }

    public static TheoryData<EventSubscription> InvalidSubscriptions() =>
    [
        // Anahtar modülle başlamıyor veya biçimsiz.
        EventSubscription.For<NoopSubscriber>("crm.listener", [DomainEventTypes.RecordUpdated], [], []),
        EventSubscription.For<NoopSubscriber>("sales.Listener", [DomainEventTypes.RecordUpdated], [], []),
        EventSubscription.For<NoopSubscriber>("sales", [DomainEventTypes.RecordUpdated], [], []),

        // Olay türü yok veya bilinmiyor.
        EventSubscription.For<NoopSubscriber>("sales.listener", [], [], []),
        EventSubscription.For<NoopSubscriber>("sales.listener", ["StatusChanged"], [], []),

        // Kayıt türü bilinmiyor.
        EventSubscription.For<NoopSubscriber>("sales.listener", [DomainEventTypes.RecordUpdated], ["crm.yok"], []),

        // Başka modülün izni.
        EventSubscription.For<NoopSubscriber>("sales.listener", [DomainEventTypes.RecordUpdated], [], [CoreModule.Permissions.ManageUsers]),

        // Abone sınıfı IEventSubscriber değil; olayın güncel sürümünü okuyamıyor.
        new("sales.listener", [DomainEventTypes.RecordUpdated], [], [], typeof(string)),
        EventSubscription.For<NoopSubscriber>("sales.listener", [DomainEventTypes.RecordUpdated], [], []) with { MaxSchemaVersion = 0 },
    ];

    [Theory]
    [MemberData(nameof(InvalidSubscriptions))]
    public void Invalid_event_subscription_is_rejected(EventSubscription subscription)
    {
        var sales = new TestModule("sales", ["sales.orders.read"]) { EventSubscriptions = [subscription] };

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(sales));
    }

    [Fact]
    public void Assignment_email_follows_the_record_types_modules_assign_to_people()
    {
        var sales = new TestModule("sales", [])
        {
            AuditedEntities = [new AuditedEntity("sales.order", "Sipariş", null, new Dictionary<string, string>())],
            OwnedRecords = [new OwnedRecordType("sales.order", "sipariş")],
        };

        var withOwned = ModuleCatalog.Create(sales).FindSubscription(CoreModule.Subscriptions.AssignmentEmail)!.Value.Subscription;

        Assert.Equal(["sales.order"], withOwned.EntityKeys);
        Assert.False(withOwned.Ordered);
        Assert.Empty(ModuleCatalog.Create(Module("billing")).SubscriptionsFor(DomainEventTypes.RecordUpdated, "sales.order"));
        Assert.Null(ModuleCatalog.Create(Module("billing")).FindSubscription(CoreModule.Subscriptions.AssignmentEmail));
    }

    [Fact]
    public void Duplicate_event_subscription_is_rejected()
    {
        var subscription = EventSubscription.For<NoopSubscriber>("sales.listener", [DomainEventTypes.RecordUpdated], [], []);
        var sales = new TestModule("sales", []) { EventSubscriptions = [subscription, subscription] };

        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(sales));
    }

    private static IModule Module(string key, params string[] permissions) => new TestModule(key, permissions);

    private sealed class NoopSubscriber : IEventSubscriber
    {
        public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestModule(string key, string[] permissions, IReadOnlyList<RoleTemplate>? templates = null) : IModule
    {
        public string Key => key;

        public string DisplayName => key;

        public IReadOnlyList<PermissionDefinition> Permissions { get; } =
            permissions.Select(p => new PermissionDefinition(p, p)).ToList();

        public IReadOnlyList<RoleTemplate> RoleTemplates { get; } = templates ?? [];

        public IReadOnlyList<SensitiveFields> SensitiveFields { get; init; } = [];

        public IReadOnlyList<SharedRecordAccess> SharedRecordAccess { get; init; } = [];

        public IReadOnlyList<SharedRecordReference> SharedRecordReferences { get; init; } = [];

        public IReadOnlyList<AuditedEntity> AuditedEntities { get; init; } = [];

        public IReadOnlyList<EventSubscription> EventSubscriptions { get; init; } = [];

        public IReadOnlyList<OwnedRecordType> OwnedRecords { get; init; } = [];
    }
}
