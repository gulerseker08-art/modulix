using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Events;
using Modulix.Core.Records;

namespace Modulix.Core.Tests.Records;

/// <summary>Boru hattının adım sırası ve "ya hepsi ya hiçbiri" davranışı (veritabanısız).</summary>
public class RecordPipelineTests
{
    private const string Entity = "test.note";

    private readonly TestTenant _tenant = new() { Current = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
    private readonly FakeStore _store = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeOutbox _outbox = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private bool _licensed = true;
    private bool _permitted = true;
    private bool _inScope = true;
    private readonly HashSet<string> _hidden = [];
    private readonly List<CustomFieldInfo> _customFields = [];
    private readonly EventHandlingScope _handling = new();

    private RecordPipeline Pipeline() => new(
        _tenant,
        new RecordStoreRegistry([_store]),
        new Gate(() => _licensed),
        new Gate(() => _permitted),
        new Gate(() => _inScope),
        new HiddenFields(_hidden),
        new CustomFields(_customFields),
        _audit,
        _outbox,
        _unitOfWork,
        _handling);

    [Fact]
    public async Task Without_active_tenant_nothing_happens()
    {
        _tenant.Current = null;

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);

        Assert.Equal(RecordErrorCode.NoActiveTenant, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Unlicensed_feature_is_rejected_before_store()
    {
        _licensed = false;

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);

        Assert.Equal(RecordErrorCode.FeatureNotLicensed, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Missing_permission_is_rejected_before_store()
    {
        _permitted = false;

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);

        Assert.Equal(RecordErrorCode.PermissionDenied, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Invalid_values_are_rejected_with_field_errors()
    {
        var result = await Pipeline().CreateAsync(Command(new JsonObject()), default);

        Assert.Equal(RecordErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Contains(result.Error.FieldErrors!, e => e.Field == "text");
        AssertNothingWritten();
    }

    [Fact]
    public async Task Create_writes_record_audit_and_event_in_one_save()
    {
        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "merhaba" }), default);

        Assert.True(result.IsSuccess);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.Created, audit.Action);
        Assert.Equal(_tenant.Current!.UserId, audit.UserId);
        Assert.Equal("merhaba", audit.Changes["text"]!["new"]!.GetValue<string>());
        Assert.Equal(DomainEventTypes.RecordCreated, Assert.Single(_outbox.Events).EventType);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Update_audits_only_changed_fields()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a", ["tag"] = "x" }), default)).Value!;

        await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b", ["tag"] = "x" }), default);

        var update = _audit.Entries[^1];
        Assert.Equal(AuditAction.Updated, update.Action);
        Assert.Equal("a", update.Changes["text"]!["old"]!.GetValue<string>());
        Assert.Equal("b", update.Changes["text"]!["new"]!.GetValue<string>());
        Assert.False(update.Changes.ContainsKey("tag"));
    }

    [Fact]
    public async Task Conflict_on_save_is_reported_and_not_swallowed_as_success()
    {
        _unitOfWork.FailWith = new RecordConflictException("çakışma");

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);

        Assert.Equal(RecordErrorCode.Conflict, result.Error!.Code);
    }

    [Fact]
    public async Task Record_out_of_scope_looks_like_not_found()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default)).Value!;
        _inScope = false;

        var read = await Pipeline().GetAsync(new RecordRef(Entity, created.Id), default);
        var update = await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b" }), default);
        var delete = await Pipeline().DeleteAsync(new RecordRef(Entity, created.Id), null, default);

        Assert.Equal(RecordErrorCode.NotFound, read.Error!.Code);
        Assert.Equal(RecordErrorCode.NotFound, update.Error!.Code);
        Assert.Equal(RecordErrorCode.NotFound, delete.Error!.Code);
    }

    [Fact]
    public async Task Record_with_owner_out_of_scope_is_not_created()
    {
        _inScope = false;

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a", ["ownerId"] = Guid.NewGuid().ToString() }), default);

        Assert.Equal(RecordErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Equal("ownerId", Assert.Single(result.Error.FieldErrors!).Field);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Hidden_field_is_left_out_of_reads_but_kept_in_audit()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a", ["secret"] = 42 }), default)).Value!;
        _hidden.Add("secret");

        var read = await Pipeline().GetAsync(new RecordRef(Entity, created.Id), default);
        var page = await Pipeline().QueryAsync(new RecordQuery(Entity), default);

        Assert.False(read.Value!.Values.ContainsKey("secret"));
        Assert.False(Assert.Single(page.Value!.Items).Values.ContainsKey("secret"));
        Assert.Equal(42, _store.Records[created.Id].Values["secret"]!.GetValue<int>());
        Assert.Equal(42, _audit.Entries[0].Changes["secret"]!["new"]!.GetValue<int>());
    }

    [Fact]
    public async Task Writing_a_hidden_field_is_denied()
    {
        _hidden.Add("secret");

        var result = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a", ["secret"] = 1 }), default);

        Assert.Equal(RecordErrorCode.PermissionDenied, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Empty_hidden_field_is_ignored_and_stored_value_kept()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a", ["secret"] = 42 }), default)).Value!;
        _hidden.Add("secret");

        var result = await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b", ["secret"] = null }), default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Values.ContainsKey("secret"));
        Assert.Equal(42, _store.Records[created.Id].Values["secret"]!.GetValue<int>());
        Assert.False(_audit.Entries[^1].Changes.ContainsKey("secret"));
    }

    [Fact]
    public async Task Filtering_by_hidden_field_is_rejected()
    {
        _hidden.Add("secret");

        var result = await Pipeline().QueryAsync(
            new RecordQuery(Entity, Filters: new Dictionary<string, string> { ["secret"] = "42" }), default);

        Assert.Equal(RecordErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Equal("secret", Assert.Single(result.Error.FieldErrors!).Field);
    }

    [Fact]
    public async Task Custom_fields_are_validated_merged_and_audited_per_field()
    {
        AddCustomField("arac_tipi", CustomFieldType.Choice, required: true, options: ["tir", "kamyon"]);
        AddCustomField("km", CustomFieldType.Number);

        var missing = await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);
        Assert.Equal("custom.arac_tipi", Assert.Single(missing.Error!.FieldErrors!).Field);

        var invalid = await Pipeline().CreateAsync(
            Command(new JsonObject { ["text"] = "a", ["custom"] = new JsonObject { ["arac_tipi"] = "gemi", ["km"] = "çok", ["yok"] = 1 } }), default);
        Assert.Equal(["custom.arac_tipi", "custom.km", "custom.yok"], invalid.Error!.FieldErrors!.Select(e => e.Field).Order());
        AssertNothingWritten();

        var created = (await Pipeline().CreateAsync(
            Command(new JsonObject { ["text"] = "a", ["custom"] = new JsonObject { ["arac_tipi"] = "tir", ["km"] = 120 } }), default)).Value!;
        Assert.Equal("tir", created.Values["custom"]!["arac_tipi"]!.GetValue<string>());
        Assert.Equal("tir", _audit.Entries[^1].Changes["custom.arac_tipi"]!["new"]!.GetValue<string>());

        // Özel alan gönderilmeyen güncelleme değerleri korur; null siler; zorunlu alan boşaltılamaz.
        await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b" }), default);
        Assert.Equal(120, _store.Records[created.Id].Values["custom"]!["km"]!.GetValue<decimal>());
        await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b", ["custom"] = new JsonObject { ["km"] = null } }), default);
        Assert.False(_store.Records[created.Id].Values["custom"]!.AsObject().ContainsKey("km"));
        Assert.Equal("tir", _store.Records[created.Id].Values["custom"]!["arac_tipi"]!.GetValue<string>());
        var cleared = await Pipeline().UpdateAsync(
            new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b", ["custom"] = new JsonObject { ["arac_tipi"] = null } }), default);
        Assert.Equal(RecordErrorCode.ValidationFailed, cleared.Error!.Code);
    }

    [Fact]
    public async Task Archived_custom_field_is_kept_but_not_returned()
    {
        AddCustomField("not", CustomFieldType.Text);
        var created = (await Pipeline().CreateAsync(
            Command(new JsonObject { ["text"] = "a", ["custom"] = new JsonObject { ["not"] = "gizli değil" } }), default)).Value!;

        _customFields.Clear();
        var read = (await Pipeline().GetAsync(new RecordRef(Entity, created.Id), default)).Value!;
        await Pipeline().UpdateAsync(new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b", ["custom"] = new JsonObject() }), default);

        Assert.Empty(read.Values["custom"]!.AsObject());
        Assert.Equal("gizli değil", _store.Records[created.Id].Values["custom"]!["not"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_choice_custom_fields_can_be_filtered()
    {
        AddCustomField("arac_tipi", CustomFieldType.Choice, options: ["tir"]);
        AddCustomField("not", CustomFieldType.Text);

        var ok = await Pipeline().QueryAsync(new RecordQuery(Entity, Filters: new Dictionary<string, string> { ["custom.arac_tipi"] = "tir" }), default);
        var text = await Pipeline().QueryAsync(new RecordQuery(Entity, Filters: new Dictionary<string, string> { ["custom.not"] = "x" }), default);
        var option = await Pipeline().QueryAsync(new RecordQuery(Entity, Filters: new Dictionary<string, string> { ["custom.arac_tipi"] = "gemi" }), default);

        Assert.True(ok.IsSuccess);
        Assert.Equal("tir", _store.LastQuery!.CustomFilters!["arac_tipi"]);
        Assert.Empty(_store.LastQuery.Filters!);
        Assert.Equal(RecordErrorCode.ValidationFailed, text.Error!.Code);
        Assert.Equal(RecordErrorCode.ValidationFailed, option.Error!.Code);
    }

    [Fact]
    public void Custom_field_keys_are_derived_from_turkish_labels()
    {
        Assert.Equal("arac_tipi", CustomFieldRules.KeyFrom("Araç tipi", new HashSet<string>()));
        Assert.Equal("guzergah_2", CustomFieldRules.KeyFrom(" Güzergâh! ", new HashSet<string> { "guzergah" }));
        Assert.Equal("a_2026_hedefi", CustomFieldRules.KeyFrom("2026 hedefi", new HashSet<string>()));
        Assert.Equal("alan", CustomFieldRules.KeyFrom("—", new HashSet<string>()));
    }

    [Fact]
    public async Task Unknown_entity_is_rejected()
    {
        var result = await Pipeline().GetAsync(new RecordRef("crm.yok", Guid.NewGuid()), default);

        Assert.Equal(RecordErrorCode.UnknownEntity, result.Error!.Code);
    }

    [Fact]
    public async Task Delete_is_audited_with_old_values()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default)).Value!;

        var result = await Pipeline().DeleteAsync(new RecordRef(Entity, created.Id), null, default);

        Assert.True(result.IsSuccess);
        var delete = _audit.Entries[^1];
        Assert.Equal(AuditAction.Deleted, delete.Action);
        Assert.Equal("a", delete.Changes["text"]!["old"]!.GetValue<string>());
        Assert.False(((JsonObject)delete.Changes["text"]!).ContainsKey("new"));
        Assert.Equal(DomainEventTypes.RecordDeleted, _outbox.Events[^1].EventType);
    }

    [Fact]
    public async Task Events_carry_sequence_actor_source_and_reason()
    {
        var created = (await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default)).Value!;
        await Pipeline().UpdateAsync(
            new RecordCommand(Entity, created.Id, new JsonObject { ["text"] = "b" }, Source: ExecutionSource.Import) { Reason = EventReasons.Transfer },
            default);
        await Pipeline().DeleteAsync(new RecordRef(Entity, created.Id), null, default, ExecutionSource.AiTool);

        var (create, update, delete) = (_outbox.Events[0], _outbox.Events[1], _outbox.Events[2]);
        Assert.Equal([1L, 2L, 3L], [create.Sequence!.Value, update.Sequence!.Value, delete.Sequence!.Value]);
        Assert.All(_outbox.Events, e => Assert.Equal(_tenant.Current!.UserId, e.UserId));
        Assert.All(_outbox.Events, e => Assert.Equal(1, e.SchemaVersion));
        Assert.All(_outbox.Events, e => Assert.Equal(0, e.Depth));
        Assert.All(_outbox.Events, e => Assert.Null(e.CausationId));
        Assert.Equal([ExecutionSource.Api, ExecutionSource.Import, ExecutionSource.AiTool], _outbox.Events.Select(e => e.Source));
        Assert.Equal([null, EventReasons.Transfer, null], _outbox.Events.Select(e => e.Reason));

        // Silmenin kaynağı da komuttan gelir; denetim kaydı olayla aynıdır.
        Assert.Equal(ExecutionSource.AiTool, _audit.Entries[^1].Source);
    }

    [Fact]
    public async Task Change_made_by_subscriber_is_linked_to_triggering_event()
    {
        var trigger = new EventEnvelope(
            Guid.NewGuid(), _tenant.Current!.TenantId, DomainEventTypes.RecordUpdated, 1, "crm.company", Guid.NewGuid(), 4,
            Guid.NewGuid(), ExecutionSource.Api, "iz-123", CausationId: null, Depth: 2, new JsonObject(), DateTimeOffset.UtcNow);
        _handling.Begin(trigger, "test.subscription");

        await Pipeline().CreateAsync(Command(new JsonObject { ["text"] = "a" }), default);

        var produced = Assert.Single(_outbox.Events);
        Assert.Equal(trigger.Id, produced.CausationId);
        Assert.Equal("iz-123", produced.CorrelationId);
        Assert.Equal(3, produced.Depth);
        Assert.Equal(ExecutionSource.BackgroundJob, produced.Source);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(ExecutionSource.BackgroundJob, audit.Source);
        Assert.Equal("iz-123", audit.CorrelationId);
    }

    private static RecordCommand Command(JsonObject values) => new(Entity, null, values);

    private void AddCustomField(string key, CustomFieldType type, bool required = false, string[]? options = null) =>
        _customFields.Add(new CustomFieldInfo(
            Guid.NewGuid(), Entity, key, key, type, required, ShowInList: false, _customFields.Count, Archived: false,
            [.. (options ?? []).Select(o => new CustomFieldOption(o, o))]));

    private void AssertNothingWritten()
    {
        Assert.Empty(_store.Records);
        Assert.Empty(_audit.Entries);
        Assert.Empty(_outbox.Events);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    private sealed class TestTenant : ITenantContextAccessor
    {
        public TenantContext? Current { get; set; }
    }

    private sealed class Gate(Func<bool> allow) : IEntitlementService, IPermissionService, IDataScopeService
    {
        public Task<bool> CanUseAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) => Task.FromResult(allow());

        public Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(allow());

        // Kapsam dışı: hiçbir sorumlunun kaydı görünmez.
        public Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>?>(allow() ? null : new HashSet<Guid>());
    }

    private sealed class HiddenFields(IReadOnlySet<string> hidden) : IFieldAccessService
    {
        public Task<IReadOnlySet<string>> GetHiddenFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult(hidden);
    }

    private sealed class CustomFields(IReadOnlyList<CustomFieldInfo> fields) : ICustomFieldProvider
    {
        public Task<IReadOnlyList<CustomFieldInfo>> GetFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomFieldInfo>>([.. fields]);
    }

    private sealed class FakeStore : IRecordStore
    {
        public Dictionary<Guid, RecordData> Records { get; } = [];

        public string EntityKey => Entity;

        public string FeatureKey => "test.notes";

        public string? OwnerField => "ownerId";

        public bool SupportsCustomFields => true;

        public RecordQuery? LastQuery { get; private set; }

        public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values) =>
            values.ContainsKey("text") ? [] : [new RecordFieldError("text", "Zorunlu.")];

        public Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Records.TryGetValue(id, out var data) ? data with { Values = (JsonObject)data.Values.DeepClone() } : null);

        public Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new RecordPage(Records.Values.ToList(), query.Page, query.PageSize, Records.Count));
        }

        public Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
        {
            var data = new RecordData(Entity, Guid.NewGuid(), 1, (JsonObject)values.DeepClone());
            Records[data.Id] = data;
            return Task.FromResult(data);
        }

        public Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
        {
            if (!Records.TryGetValue(id, out var current))
            {
                return Task.FromResult<RecordData?>(null);
            }

            // Kısmi güncelleme (gerçek depolar gibi): verilmeyen alan korunur.
            var merged = (JsonObject)current.Values.DeepClone();
            foreach (var (name, node) in values)
            {
                merged[name] = node?.DeepClone();
            }

            var updated = current with { Version = current.Version + 1, Values = merged };
            Records[id] = updated;
            return Task.FromResult<RecordData?>(updated);
        }

        public Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken) =>
            Task.FromResult(Records.Remove(id));
    }

    private sealed class FakeAudit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Add(AuditEntry entry) => Entries.Add(entry);
    }

    private sealed class FakeOutbox : IDomainEventOutbox
    {
        public List<DomainEvent> Events { get; } = [];

        public void Add(DomainEvent domainEvent) => Events.Add(domainEvent);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Exception? FailWith { get; set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Saves++;
            return Task.CompletedTask;
        }
    }
}

public class RecordDiffTests
{
    [Fact]
    public void Unchanged_fields_are_left_out()
    {
        var diff = RecordDiff.Compute(
            new JsonObject { ["a"] = 1, ["b"] = "x" },
            new JsonObject { ["a"] = 2, ["b"] = "x" });

        Assert.Equal(new[] { "a" }, diff.Select(p => p.Key).ToArray());
        Assert.Equal(1, diff["a"]!["old"]!.GetValue<int>());
        Assert.Equal(2, diff["a"]!["new"]!.GetValue<int>());
    }

    [Fact]
    public void Added_and_removed_fields_are_recorded()
    {
        var diff = RecordDiff.Compute(new JsonObject { ["a"] = 1 }, new JsonObject { ["b"] = 2 });

        Assert.Null(diff["a"]!["new"]);
        Assert.Null(diff["b"]!["old"]);
        Assert.Equal(2, diff.Count);
    }
}
