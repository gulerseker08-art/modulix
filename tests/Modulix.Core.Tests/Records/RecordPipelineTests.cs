using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
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

    private RecordPipeline Pipeline() => new(
        _tenant,
        new RecordStoreRegistry([_store]),
        new Gate(() => _licensed),
        new Gate(() => _permitted),
        new Gate(() => _inScope),
        _audit,
        _outbox,
        _unitOfWork);

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

    private static RecordCommand Command(JsonObject values) => new(Entity, null, values);

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

        public Task<bool> CanAccessAsync(TenantContext tenant, RecordData record, RecordOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(allow());
    }

    private sealed class FakeStore : IRecordStore
    {
        public Dictionary<Guid, RecordData> Records { get; } = [];

        public string EntityKey => Entity;

        public string FeatureKey => "test.notes";

        public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values) =>
            values.ContainsKey("text") ? [] : [new RecordFieldError("text", "Zorunlu.")];

        public Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Records.TryGetValue(id, out var data) ? data with { Values = (JsonObject)data.Values.DeepClone() } : null);

        public Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new RecordPage(Records.Values.ToList(), query.Page, query.PageSize, Records.Count));

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

            var updated = current with { Version = current.Version + 1, Values = (JsonObject)values.DeepClone() };
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
