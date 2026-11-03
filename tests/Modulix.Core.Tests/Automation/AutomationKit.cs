using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Settings;

namespace Modulix.Core.Tests.Automation;

/// <summary>Kural altyapısı testlerinin ortak sahte modülü, alan kümesi ve yardımcıları. Core sektöre özel bir şey bilmez: "sales" yalnızca testtir.</summary>
internal static class AutomationKit
{
    public const string Entity = "sales.deal";
    public const string Activity = "sales.activity";
    public const string SecretPermission = "sales.secret.read";
    public const string WritePermission = "sales.deals.write";

    public static readonly AutomationFieldDefinition Amount = new(Entity, "amount", "Tutar", AutomationFieldType.Money) { CurrencyField = "currency" };

    public static readonly AutomationFieldDefinition Stage = new(Entity, "stage", "Aşama", AutomationFieldType.Choice)
    {
        Options = [new("new", "Yeni"), new("negotiation", "Müzakere"), new("won", "Kazanıldı"), new("lost", "Kaybedildi")],
    };

    public static readonly AutomationFieldDefinition DueDate = new(Entity, "dueDate", "Kapanış tarihi", AutomationFieldType.Date);
    public static readonly AutomationFieldDefinition LastContact = new(Entity, "lastContactAt", "Son görüşme", AutomationFieldType.DateTime);
    public static readonly AutomationFieldDefinition Owner = new(Entity, "ownerId", "Sorumlu", AutomationFieldType.Lookup) { LookupKey = "sales.owner" };
    public static readonly AutomationFieldDefinition Title = new(Entity, "title", "Başlık", AutomationFieldType.Text);
    public static readonly AutomationFieldDefinition Score = new(Entity, "score", "Puan", AutomationFieldType.Number);
    public static readonly AutomationFieldDefinition IsVip = new(Entity, "isVip", "Öncelikli müşteri", AutomationFieldType.Boolean);
    public static readonly AutomationFieldDefinition Secret = new(Entity, "margin", "Kâr marjı", AutomationFieldType.Number) { ReadPermission = SecretPermission };

    public static readonly AutomationFieldDefinition ActivityKind = new(Activity, "kind", "Tür", AutomationFieldType.Choice)
    {
        Options = [new("call", "Arama"), new("meeting", "Toplantı")],
    };

    public static readonly AutomationFieldDefinition ActivityDate = new(Activity, "doneAt", "Tarih", AutomationFieldType.Date);

    public static IReadOnlyList<AutomationFieldDefinition> DealFields { get; } =
        [Amount, Stage, DueDate, LastContact, Owner, Title, Score, IsVip, Secret];

    public static AutomationRelationDefinition Activities { get; } =
        new("sales.deal.activities", Entity, Activity, "Aktiviteler", [ActivityKind, ActivityDate], typeof(NoopResolver));

    public static AutomationFieldSet Fields(params string[] granted) =>
        new(Entity, DealFields, [Activities], granted.ToHashSet(StringComparer.Ordinal));

    public static ConditionLeaf Leaf(AutomationFieldDefinition field, AutomationOperator op, params string[] values) =>
        new(field.Key, op, values);

    public static ConditionGroup And(params ConditionNode[] items) => new(ConditionLogic.And, items);

    public static ConditionGroup Or(params ConditionNode[] items) => new(ConditionLogic.Or, items);

    /// <summary>Verilen yerel günün öğle saatinde, İstanbul saat diliminde bir "bugün".</summary>
    public static TenantDay Day(int year, int month, int day)
    {
        var zone = RegionalTime.Find(null);
        var local = new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified);
        return new TenantDay(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime(), zone);
    }

    public static RecordFacts Facts(AutomationFieldSet fields, JsonObject values, JsonObject? changes = null, int? relationCount = null) =>
        new(fields, Guid.NewGuid(), values, changes, new FixedCounter(relationCount));

    public static ConditionExplanation Evaluate(ConditionNode? node, AutomationFieldSet fields, IAutomationFacts facts, TenantDay day) =>
        ConditionEvaluator.EvaluateAsync(node, fields, facts, day, CancellationToken.None).GetAwaiter().GetResult();

    public sealed class FixedCounter(int? count) : IAutomationRelationCounter
    {
        public Task<int?> CountAsync(Guid recordId, ConditionRelation relation, CancellationToken cancellationToken) => Task.FromResult(count);
    }

    public sealed class NoopResolver : IAutomationRelationResolver
    {
        public Task<int> CountAsync(AutomationRelationQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public sealed class NoopAction : IAutomationAction
    {
        public Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Önizleme ve doğrulama aksiyon işleyicisini çağırmamalı.");
    }

    public static AutomationActionDefinition FollowUp { get; } = new(
        "sales.follow-up.create",
        "Takip görevi oluştur",
        "Kayıt sorumlusuna takip görevi açar.",
        [
            new AutomationParameter("subject", "Konu", AutomationFieldType.Text),
            new AutomationParameter("dueInDays", "Kaç gün sonra", AutomationFieldType.Number),
            new AutomationParameter("priority", "Önem", AutomationFieldType.Choice, Required: false)
            {
                Options = [new("normal", "Normal"), new("high", "Yüksek")],
            },
            new AutomationParameter("note", "Not", AutomationFieldType.Text, Required: false) { AllowFieldBinding = false },
            new AutomationParameter("ownerId", "Atanan", AutomationFieldType.Lookup, Required: false) { LookupKey = "sales.owner" },
        ],
        [WritePermission],
        [],
        AutomationSideEffect.Write,
        SupportsDryRun: false);

    public static AutomationActionDefinition Notify { get; } = new(
        "sales.notify.send",
        "Bildirim gönder",
        "Kullanıcıya uygulama içi bildirim gönderir.",
        [new AutomationParameter("message", "İleti", AutomationFieldType.Text)],
        [],
        ["billing"],
        AutomationSideEffect.Write,
        SupportsDryRun: true);

    public static AutomationCapabilities Capabilities { get; } = new()
    {
        Entities =
        [
            new(Entity, "Fırsat", "sales.deals.read"),
            new(Activity, "Aktivite"),
        ],
        Events =
        [
            new("sales.deal.created", Entity, DomainEventTypes.RecordCreated, "Fırsat oluşturuldu"),
            new("sales.deal.updated", Entity, DomainEventTypes.RecordUpdated, "Fırsat güncellendi"),
            new("sales.deal.won", Entity, "sales.deal.won", "Fırsat kazanıldı"),
        ],
        Fields = DealFields,
        Relations = [Activities],
        Actions =
        [
            AutomationActionRegistration.For<NoopAction>(FollowUp),
            AutomationActionRegistration.For<NoopAction>(Notify),
        ],
    };

    public static ModuleCatalog Catalog(AutomationCapabilities? capabilities = null, params IModule[] more) =>
        new([
            new CoreModule(),
            new TestModule("sales", capabilities ?? Capabilities, "sales.deals.read", WritePermission, SecretPermission),
            new TestModule("billing", AutomationCapabilities.None, "billing.invoices.read"),
            .. more,
        ]);

    public sealed class TestModule(string key, AutomationCapabilities capabilities, params string[] permissions) : IModule
    {
        public string Key => key;

        public string DisplayName => key;

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [.. permissions.Select(p => new PermissionDefinition(p, p))];

        public IReadOnlyList<LookupDefinition> Lookups { get; init; } = key == "sales"
            ? [new LookupDefinition("sales.owner", Entity, "ownerId", "Sorumlu", LookupTargetKind.Member)]
            : [];

        public AutomationCapabilities Automation => capabilities;
    }

    public sealed class FakeAccess(IEnumerable<string> permissions, IEnumerable<string> enabledModules) : ICurrentAccess
    {
        private readonly HashSet<string> granted = [.. permissions];
        private readonly HashSet<string> enabled = [.. enabledModules];

        public Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken) =>
            Task.FromResult(moduleKey == CoreModule.ModuleKey || enabled.Contains(moduleKey));

        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => Task.FromResult(granted.Contains(permission));

        public Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(granted);

        public Task<RecordScope> GetScopeAsync(CancellationToken cancellationToken) => Task.FromResult(RecordScope.All);
    }

    public sealed class NoCustomFields : ICustomFieldService
    {
        public Task<IReadOnlyList<CustomizableEntity>> ListEntitiesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CustomizableEntity>>([]);

        public Task<IReadOnlyList<CustomFieldInfo>> ListAsync(string? entityKey, bool includeArchived, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomFieldInfo>>([]);

        public Task<CustomFieldInfo> CreateAsync(string entityKey, CustomFieldInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomFieldInfo?> UpdateAsync(Guid id, CustomFieldInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomFieldInfo?> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReorderAsync(string entityKey, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public static AutomationCatalogService CatalogService(ModuleCatalog modules, ICurrentAccess access) =>
        new(modules, access, new NoCustomFields());

    public static AutomationDefinitionValidator Validator(ModuleCatalog modules, ICurrentAccess access) =>
        new(modules, CatalogService(modules, access), access);

    /// <summary>Tek aksiyonlu, geçerli bir tanım.</summary>
    public static AutomationDefinition ValidDefinition(ConditionNode? condition = null, AutomationTrigger? trigger = null) =>
        new(
            trigger ?? new RecordCreatedTrigger(),
            condition,
            [new AutomationActionStep("follow-up", FollowUp.Key, new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 2 })]);
}
