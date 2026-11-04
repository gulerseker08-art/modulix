using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Events;
using Modulix.Core.Records;

namespace Modulix.Core.Tests.Records;

/// <summary>
/// Birleştirme adımları (ADR-0013, veritabanısız): izin, kapsam, çözülemeyen başvuru ve depo hatasında hiçbir şey yazılmaz;
/// başarıda audit, olay ve kayıt tek seferde yazılır; tekrarlanan istek sessizce başarılı olmaz.
/// </summary>
public class RecordMergePipelineTests
{
    private const string Entity = "test.company";

    private readonly TenantContext _tenant = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private readonly MergeStore _store = new();
    private readonly FakeReferences _references = new();
    private readonly Audit _audit = new();
    private readonly Outbox _outbox = new();
    private readonly Unit _unit = new();
    private bool _permitted = true;
    private bool _inScope = true;

    private RecordPipeline Pipeline() => new(
        new Tenant(_tenant),
        new Company(),
        new RecordStoreRegistry([_store]),
        new Gate(() => true, () => _permitted, () => _inScope),
        new Gate(() => true, () => _permitted, () => _inScope),
        new Gate(() => true, () => _permitted, () => _inScope),
        new NoHidden(),
        new NoCustom(),
        _audit,
        _outbox,
        _unit,
        new EventHandlingScope(),
        _references);

    private MergeRequest Request() => new(_store.SourceId, _store.TargetId, 1, 1);

    [Fact]
    public async Task Merge_writes_both_audit_entries_and_events_and_saves_once()
    {
        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _unit.Saves);
        Assert.Equal(1, _store.Merges);
        Assert.Equal(1, _references.Applied);
        Assert.Equal([_store.TargetId, _store.SourceId], _audit.Entries.Select(e => e.RecordId));
        Assert.All(_audit.Entries, e => Assert.Equal(AuditAction.Merged, e.Action));
        Assert.Equal([DomainEventTypes.RecordUpdated, DomainEventTypes.RecordMerged], _outbox.Events.Select(e => e.EventType));
        Assert.Equal(_store.TargetId.ToString(), _audit.Entries[1].Changes["mergedInto"]!["new"]!.GetValue<string>());
        Assert.Equal(_store.SourceId.ToString(), _audit.Entries[0].Changes["mergedFrom"]!["new"]!.GetValue<string>());
    }

    [Fact]
    public async Task Merge_without_its_permission_changes_nothing()
    {
        _permitted = false;

        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.Equal(RecordErrorCode.PermissionDenied, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Merge_with_the_source_out_of_scope_is_not_found_and_changes_nothing()
    {
        _inScope = false;

        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.Equal(RecordErrorCode.NotFound, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task An_unresolved_reference_from_another_module_refuses_the_merge_before_the_store_runs()
    {
        _references.Blockers = ["3 sevkiyat bu kayda başvuruyor ve taşınamıyor."];

        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.Equal(RecordErrorCode.ValidationFailed, result.Error!.Code);
        Assert.Contains("sevkiyat", result.Error.FieldErrors!.Single().Message);
        Assert.Equal(0, _store.Merges);
        Assert.Equal(0, _references.Applied);
        AssertNothingWritten();

        var preview = await Pipeline().PreviewMergeAsync(Entity, Request(), default);
        Assert.Single(preview.Value!.Blockers);
    }

    [Fact]
    public async Task A_conflict_or_validation_error_from_the_store_writes_nothing()
    {
        _store.FailWith = new RecordConflictException("Kayıt siz düzenlerken değişti.");
        var conflict = await Pipeline().MergeAsync(Entity, Request(), default);
        Assert.Equal(RecordErrorCode.Conflict, conflict.Error!.Code);

        _store.FailWith = new RecordValidationException([new RecordFieldError("merge", "Lokasyon kodu çakışıyor.")]);
        var invalid = await Pipeline().MergeAsync(Entity, Request(), default);
        Assert.Equal(RecordErrorCode.ValidationFailed, invalid.Error!.Code);

        AssertNothingWritten();
    }

    [Fact]
    public async Task A_failed_save_leaves_no_partial_result()
    {
        _unit.FailWith = new RecordConflictException("Aynı anahtarla bir kayıt zaten var.");

        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.Equal(RecordErrorCode.Conflict, result.Error!.Code);
        Assert.Equal(0, _unit.Saves);
    }

    [Fact]
    public async Task Repeating_a_completed_merge_reports_the_trace_instead_of_succeeding()
    {
        _store.Records.Remove(_store.SourceId);
        _store.Link = new MergeLink(Entity, _store.SourceId, _store.TargetId, DateTimeOffset.UtcNow, null);

        var result = await Pipeline().MergeAsync(Entity, Request(), default);

        Assert.Equal(RecordErrorCode.Conflict, result.Error!.Code);
        Assert.Contains("zaten birleştirilmiş", result.Error.Message);
        Assert.Equal(0, _store.Merges);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_record_cannot_be_merged_into_itself()
    {
        var result = await Pipeline().MergeAsync(Entity, new MergeRequest(_store.SourceId, _store.SourceId, 1, 1), default);

        Assert.Equal(RecordErrorCode.ValidationFailed, result.Error!.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task The_trace_of_a_merged_id_is_hidden_when_the_target_is_out_of_scope()
    {
        _store.Records.Remove(_store.SourceId);
        _store.Link = new MergeLink(Entity, _store.SourceId, _store.TargetId, DateTimeOffset.UtcNow, null);
        Assert.True((await Pipeline().ResolveMergedAsync(new RecordRef(Entity, _store.SourceId), default)).IsSuccess);

        _inScope = false;
        var hidden = await Pipeline().ResolveMergedAsync(new RecordRef(Entity, _store.SourceId), default);

        Assert.Equal(RecordErrorCode.NotFound, hidden.Error!.Code);
    }

    private void AssertNothingWritten()
    {
        Assert.Empty(_audit.Entries);
        Assert.Empty(_outbox.Events);
        Assert.Equal(0, _unit.Saves);
    }

    private sealed class Tenant(TenantContext current) : ITenantContextAccessor
    {
        public TenantContext? Current { get; } = current;
    }

    private sealed class Company : ICompanyContextAccessor
    {
        public CompanyContext? Current => null;
    }

    private sealed class Gate(Func<bool> licensed, Func<bool> permitted, Func<bool> inScope) : IEntitlementService, IPermissionService, IDataScopeService
    {
        public Task<bool> CanUseAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) => Task.FromResult(licensed());

        public Task<bool> HasPermissionAsync(TenantContext tenant, string entityKey, RecordOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(permitted());

        public Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>?>(inScope() ? null : new HashSet<Guid>());
    }

    private sealed class NoHidden : IFieldAccessService
    {
        public Task<IReadOnlySet<string>> GetHiddenFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class NoCustom : ICustomFieldProvider
    {
        public Task<IReadOnlyList<CustomFieldInfo>> GetFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomFieldInfo>>([]);
    }

    private sealed class FakeReferences : ISharedReferenceMerger
    {
        public IReadOnlyList<string> Blockers { get; set; } = [];

        public int Applied { get; private set; }

        public Task<SharedReferencePlan> PlanAsync(string sharedEntityKey, string ownerPrefix, Guid fromId, CancellationToken cancellationToken) =>
            Task.FromResult(new SharedReferencePlan([new MergeImpact("başvuru taşınır", 2)], Blockers));

        public Task ApplyAsync(string sharedEntityKey, string ownerPrefix, Guid fromId, Guid toId, CancellationToken cancellationToken)
        {
            Applied++;
            return Task.CompletedTask;
        }
    }

    private sealed class MergeStore : IMergeableRecordStore
    {
        public MergeStore()
        {
            foreach (var id in new[] { SourceId, TargetId })
            {
                Records[id] = new RecordData(Entity, id, 1, new JsonObject { ["name"] = id == SourceId ? "Kaynak" : "Hedef", ["ownerId"] = null });
            }
        }

        public Guid SourceId { get; } = Guid.NewGuid();

        public Guid TargetId { get; } = Guid.NewGuid();

        public Dictionary<Guid, RecordData> Records { get; } = [];

        public int Merges { get; private set; }

        public Exception? FailWith { get; set; }

        public MergeLink? Link { get; set; }

        public string EntityKey => Entity;

        public string FeatureKey => Entity;

        public string? OwnerField => "ownerId";

        public string MergePermission => "test.companies.merge";

        public string? SharedEntityKey => "core.party";

        public Task<Guid?> SharedIdOfAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Guid?>(id);

        public Task<MergeLink?> FindMergeLinkAsync(Guid sourceId, CancellationToken cancellationToken) => Task.FromResult(Link);

        public Task<MergePreview?> PreviewMergeAsync(MergeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<MergePreview?>(new MergePreview(Records[SourceId], Records[TargetId], [], [], []));

        public Task<MergeOutcome?> MergeAsync(MergeRequest request, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Merges++;
            var target = Records[TargetId];
            return Task.FromResult<MergeOutcome?>(new MergeOutcome(Records[SourceId], target, target with { Version = target.Version + 1 }, [new MergeImpact("kayıt taşınır", 1)]));
        }

        public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values) => [];

        public Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Records.TryGetValue(id, out var data) ? data with { Values = (JsonObject)data.Values.DeepClone() } : null);

        public Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new RecordPage([], 1, 1, 0));

        public Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Audit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Add(AuditEntry entry) => Entries.Add(entry);
    }

    private sealed class Outbox : IDomainEventOutbox
    {
        public List<DomainEvent> Events { get; } = [];

        public void Add(DomainEvent domainEvent) => Events.Add(domainEvent);
    }

    private sealed class Unit : IUnitOfWork
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
