using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Modules.Logistics.Automation;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>
/// Lojistiğin başlangıç kuralı şablonları (ADR-0038 bölüm 23). Gerçek katalog ve gerçek tanım doğrulayıcısıyla: şablonlar yalnızca bildirim yazar,
/// alıcıyı firmaya bırakır ve alıcı seçilince eksiksiz doğrulanır. Veritabanı gerektiren uçtan uca yol entegrasyon testindedir
/// (<c>LogisticsAutomationTemplateTests</c>).
/// </summary>
public class LogisticsAutomationTemplateTests
{
    private const string RecipientPath = "actions[0].parameters.recipientId";

    private static readonly ModuleCatalog Catalog = ModuleCatalog.Create(new LogisticsModule());

    private static string[] AllPermissions => [.. Catalog.Modules.SelectMany(m => m.Permissions).Select(p => p.Key)];

    private static AutomationDefinitionValidator Validator(ICurrentAccess access) =>
        new(Catalog, new AutomationCatalogService(Catalog, access, new NoCustomFields()), access);

    private static Task<IReadOnlyList<AutomationIssue>> ValidateAsync(AutomationTemplateDefinition template, AutomationDefinition definition, ICurrentAccess access) =>
        Validator(access).ValidateAsync(LogisticsModule.ModuleKey, template.EntityKey, definition, CancellationToken.None);

    [Fact]
    public void The_catalog_registers_every_template_in_order_under_the_module_prefix()
    {
        var registered = Catalog.AutomationTemplates.Where(t => t.ModuleKey == LogisticsModule.ModuleKey).Select(t => t.Template.Key).ToList();

        Assert.Equal(LogisticsAutomationTemplates.All.Select(t => t.Key), registered);
        Assert.Equal(10, registered.Count);
        Assert.Equal(registered.Count, registered.Distinct(StringComparer.Ordinal).Count());
        Assert.All(registered, key => Assert.StartsWith(LogisticsModule.ModuleKey + ".", key));
    }

    [Fact]
    public void Templates_only_notify_and_leave_exactly_the_recipient_to_the_company()
    {
        Assert.Equal(NotifyAutomation.NotifyAction, LogisticsAutomationTemplates.NotifyAction);

        foreach (var template in LogisticsAutomationTemplates.All)
        {
            var step = Assert.Single(template.Definition.Actions);
            Assert.Equal(LogisticsAutomationTemplates.NotifyAction, step.Action);
            Assert.Equal(["title"], step.Parameters.Select(p => p.Key));
            Assert.Equal(
                [new AutomationTemplateBlank(LogisticsAutomationTemplates.NotifyStep, LogisticsAutomationTemplates.RecipientParameter)], template.ToComplete);
            Assert.Equal([RecipientPath], AutomationTemplateService.BlankPaths(template));
        }
    }

    [Fact]
    public async Task The_missing_recipient_is_the_only_issue_and_choosing_one_makes_every_template_valid()
    {
        var access = new FakeAccess(AllPermissions, [LogisticsModule.ModuleKey]);

        foreach (var template in LogisticsAutomationTemplates.All)
        {
            var draft = await ValidateAsync(template, template.Definition, access);
            Assert.True(draft.Select(i => i.Path).SequenceEqual([RecipientPath]), $"{template.Key}: {string.Join(" | ", draft.Select(i => $"{i.Path}: {i.Message}"))}");

            // Firma alıcıyı seçince (taslakta) tanım yayın doğrulamasından geçer: tetikleyici, koşul ve metin değişkenleri beyanla uyumlu.
            var completed = AutomationJson.Deserialize(AutomationJson.Serialize(template.Definition));
            completed.Actions[0].Parameters[LogisticsAutomationTemplates.RecipientParameter] = Guid.NewGuid().ToString();
            var ready = await ValidateAsync(template, completed, access);
            Assert.True(ready.Count == 0, $"{template.Key}: {string.Join(" | ", ready.Select(i => $"{i.Path}: {i.Message}"))}");
        }
    }

    [Fact]
    public async Task Templates_are_listed_as_available_only_with_the_entity_read_permission_and_an_open_module()
    {
        async Task<IReadOnlyList<AutomationTemplateView>> ListAsync(ICurrentAccess access) =>
            await new AutomationTemplateService(Catalog, access, Validator(access), null!).ListAsync(CancellationToken.None); // listeleme kural servisini kullanmaz

        var all = await ListAsync(new FakeAccess(AllPermissions, [LogisticsModule.ModuleKey]));
        Assert.Equal(LogisticsAutomationTemplates.All.Count, all.Count);
        Assert.All(all, t => Assert.True(t.Available, $"{t.Key}: {string.Join(" | ", t.Issues.Select(i => i.Message))}"));

        // Sevkiyat okuma izni olmayan kişi sevkiyat şablonlarını kullanamaz; diğerleri açık kalır (yetki yükseltme yok).
        var limited = await ListAsync(new FakeAccess(AllPermissions.Where(p => p != LogisticsModule.Permissions.ShipmentsRead), [LogisticsModule.ModuleKey]));
        Assert.All(limited, t => Assert.Equal(t.EntityKey != LogisticsModule.Entities.Shipment, t.Available));
        Assert.Contains(limited, t => !t.Available);

        // Lojistik kapalıyken şablon listelenmez.
        Assert.Empty(await ListAsync(new FakeAccess(AllPermissions, [])));
    }

    [Fact]
    public void Scan_templates_use_the_date_fields_the_record_source_supports_and_event_templates_use_declared_events()
    {
        // Tarama kaynağının (LogisticsAutomationRecordSource) desteklediği tarih alanları; kaynak başka alanı "Desteklenmeyen tarih alanı" ile reddeder.
        var scanned = new Dictionary<string, string[]>
        {
            [LogisticsModule.Entities.TransportRequest] = ["pickupDate", "deliveryBy"],
            [LogisticsModule.Entities.Shipment] = ["pickupDate", "deliveryBy"],
            [LogisticsModule.Entities.Trip] = ["plannedStart", "plannedEnd"],
            [LogisticsModule.Entities.TripExpense] = ["expenseDate"],
        };

        foreach (var template in LogisticsAutomationTemplates.All)
        {
            switch (template.Definition.Trigger)
            {
                case DateRelativeTrigger date:
                    Assert.StartsWith(template.EntityKey + ".", date.Field);
                    Assert.Contains(date.Field[(template.EntityKey.Length + 1)..], scanned[template.EntityKey]);
                    break;
                case DomainEventTrigger domainEvent:
                    Assert.Equal(template.EntityKey, Catalog.FindAutomationEvent(domainEvent.EventKey)!.EntityKey);
                    Assert.NotNull(Catalog.FindBusinessEvent(domainEvent.EventKey));
                    break;
                case RecordCreatedTrigger:
                    break;
                default:
                    Assert.Fail($"Beklenmeyen tetikleyici: {template.Definition.Trigger.GetType().Name}");
                    break;
            }
        }
    }

    [Fact]
    public void Template_definitions_survive_the_stored_json_form_unchanged()
    {
        foreach (var template in LogisticsAutomationTemplates.All)
        {
            var json = AutomationJson.Serialize(template.Definition);

            Assert.Equal(json, AutomationJson.Serialize(AutomationJson.Deserialize(json)));
        }
    }

    private sealed class FakeAccess(IEnumerable<string> permissions, IEnumerable<string> enabledModules) : ICurrentAccess
    {
        private readonly HashSet<string> granted = [.. permissions];
        private readonly HashSet<string> enabled = [.. enabledModules];

        public Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken) =>
            Task.FromResult(moduleKey == CoreModule.ModuleKey || enabled.Contains(moduleKey));

        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => Task.FromResult(granted.Contains(permission));

        public Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(granted);

        public Task<RecordScope> GetScopeAsync(CancellationToken cancellationToken) => Task.FromResult(RecordScope.All);
    }

    private sealed class NoCustomFields : ICustomFieldService
    {
        public Task<IReadOnlyList<CustomizableEntity>> ListEntitiesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CustomizableEntity>>([]);

        public Task<IReadOnlyList<CustomFieldInfo>> ListAsync(string? entityKey, bool includeArchived, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomFieldInfo>>([]);

        public Task<CustomFieldInfo> CreateAsync(string entityKey, CustomFieldInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomFieldInfo?> UpdateAsync(Guid id, CustomFieldInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomFieldInfo?> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReorderAsync(string entityKey, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
