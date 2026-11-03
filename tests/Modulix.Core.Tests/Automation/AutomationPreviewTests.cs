using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationPreviewTests
{
    private static readonly Guid RecordId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static JsonObject Deal() => new()
    {
        ["stage"] = "negotiation",
        ["score"] = 80,
        ["title"] = "Örnek Fırsat",
        ["ownerId"] = OwnerId.ToString(),
        ["margin"] = 12345,
    };

    private static (AutomationPreviewService Service, RecordingPipeline Pipeline) Create(string[]? permissions = null, int? relationCount = null)
    {
        var catalog = Catalog();
        var access = new FakeAccess(permissions ?? ["sales.deals.read", WritePermission], ["sales", "billing"]);
        var pipeline = new RecordingPipeline(Deal());
        var service = new AutomationPreviewService(
            Validator(catalog, access), CatalogService(catalog, access), pipeline, new FixedClock(), new FixedCounter(relationCount), catalog);
        return (service, pipeline);
    }

    private static AutomationPreview Preview(AutomationPreviewService service, AutomationDefinition definition, Guid? recordId, JsonObject? changes = null)
    {
        var result = service.PreviewAsync(new AutomationPreviewRequest("sales", Entity, definition, recordId, changes), CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private static AutomationDefinition Definition(ConditionNode? condition) => new(
        new RecordCreatedTrigger(),
        condition,
        [
            new AutomationActionStep(
                "follow-up",
                FollowUp.Key,
                new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 2, ["ownerId"] = new JsonObject { ["$field"] = Owner.Key } }),
        ]);

    [Fact]
    public void Passing_conditions_plan_the_actions_with_resolved_parameters_and_do_not_run_them()
    {
        var (service, pipeline) = Create();

        var preview = Preview(service, Definition(Leaf(Score, AutomationOperator.Gte, "50")), RecordId);

        Assert.True(preview.Evaluated);
        Assert.True(preview.ConditionsPassed);
        var planned = Assert.Single(preview.PlannedActions);
        Assert.Equal("follow-up", planned.StepId);
        Assert.Equal(OwnerId.ToString(), planned.Parameters["ownerId"]!.GetValue<string>());
        Assert.True(planned.Permitted);
        Assert.Contains("çalıştırılmadı", planned.Note);
        Assert.Equal(AutomationSideEffect.Write, planned.SideEffect);
        Assert.Equal(1, pipeline.Reads);
        Assert.Equal(0, pipeline.Writes);
    }

    [Fact]
    public void Failing_conditions_plan_nothing_and_explain_why()
    {
        var (service, _) = Create();

        var preview = Preview(service, Definition(Leaf(Score, AutomationOperator.Lt, "10")), RecordId);

        Assert.True(preview.Evaluated);
        Assert.False(preview.ConditionsPassed);
        Assert.Empty(preview.PlannedActions);
        Assert.Contains("sağlanmadı", preview.Explanation!.Text);
    }

    [Fact]
    public void Without_a_sample_record_only_the_definition_is_checked()
    {
        var (service, pipeline) = Create();

        var preview = Preview(service, Definition(Leaf(Score, AutomationOperator.Gte, "50")), recordId: null);

        Assert.False(preview.Evaluated);
        Assert.Empty(preview.Issues);
        Assert.Contains(preview.Warnings, w => w.Contains("Örnek kayıt seçilmedi"));
        Assert.Equal(0, pipeline.Reads);
    }

    [Fact]
    public void Invalid_definition_returns_issues_and_never_touches_records()
    {
        var (service, pipeline) = Create();

        var preview = Preview(service, Definition(Leaf(Score, AutomationOperator.Eq, "abc")), RecordId);

        Assert.NotEmpty(preview.Issues);
        Assert.False(preview.Evaluated);
        Assert.Equal(0, pipeline.Reads);
        Assert.Equal(0, pipeline.Writes);
    }

    [Fact]
    public void Missing_or_forbidden_sample_record_is_reported_as_the_pipeline_says()
    {
        var (service, pipeline) = Create();
        pipeline.Failure = new RecordError(RecordErrorCode.NotFound, "Kayıt bulunamadı.");

        var result = service.PreviewAsync(
            new AutomationPreviewRequest("sales", Entity, Definition(null), Guid.NewGuid(), null), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal(RecordErrorCode.NotFound, result.Error!.Code);
    }

    [Fact]
    public void Unknown_entity_or_missing_read_permission_is_refused()
    {
        var (service, _) = Create(permissions: [WritePermission]);

        var result = service.PreviewAsync(
            new AutomationPreviewRequest("sales", Entity, Definition(null), RecordId, null), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsSuccess);
        Assert.Equal(RecordErrorCode.UnknownEntity, result.Error!.Code);
    }

    [Fact]
    public void Change_conditions_warn_when_no_change_information_is_given()
    {
        var (service, _) = Create();
        var definition = new AutomationDefinition(
            new FieldChangedTrigger([Stage.Key]),
            Leaf(Stage, AutomationOperator.ChangedTo, "won"),
            [new AutomationActionStep("a", FollowUp.Key, new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 1 })]);

        var without = Preview(service, definition, RecordId);
        var with = Preview(
            service, definition, RecordId, new JsonObject { ["stage"] = new JsonObject { ["old"] = "new", ["new"] = "won" } });

        Assert.Contains(without.Warnings, w => w.Contains("Değişiklik bilgisi verilmedi"));
        Assert.False(without.ConditionsPassed);
        Assert.Empty(with.Warnings);
        Assert.True(with.ConditionsPassed);
    }

    [Fact]
    public void Condition_on_a_field_the_author_cannot_read_is_reported_and_not_evaluated()
    {
        var (service, _) = Create();

        // Yazar marj alanını göremez: doğrulama, koşulda kullanılmasını yayın öncesi reddeder.
        var preview = Preview(service, Definition(Leaf(Secret, AutomationOperator.Gt, "1")), RecordId);

        Assert.Contains(preview.Issues, i => i.Message.Contains("görme izniniz yok"));
        Assert.False(preview.Evaluated);
    }

    [Fact]
    public void Preview_texts_describe_trigger_and_condition_for_a_non_technical_reader()
    {
        var (service, _) = Create();

        var preview = Preview(service, Definition(Leaf(Stage, AutomationOperator.Eq, "negotiation")), recordId: null);

        Assert.Equal("Kayıt oluşturulunca", preview.TriggerText);
        Assert.Equal("Aşama = Müzakere", preview.ConditionText);
    }

    private sealed class FixedClock : ITenantClock
    {
        public Task<TenantDay> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Day(2026, 11, 4));
    }

    /// <summary>Yalnızca okuma kabul eder; her yazma çağrısı sayılır (önizleme sıfır yazma yapmalı).</summary>
    private sealed class RecordingPipeline(JsonObject values) : IRecordPipeline
    {
        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public RecordError? Failure { get; set; }

        public Task<RecordResult<RecordData>> GetAsync(RecordRef record, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Failure is { } error
                ? RecordResult<RecordData>.Fail(error.Code, error.Message)
                : RecordResult<RecordData>.Ok(new RecordData(record.EntityKey, record.Id, 1, values)));
        }

        public Task<RecordResult<RecordPage>> QueryAsync(RecordQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecordResult<RecordData>> CreateAsync(RecordCommand command, CancellationToken cancellationToken) => Write<RecordData>();

        public Task<RecordResult<RecordData>> UpdateAsync(RecordCommand command, CancellationToken cancellationToken) => Write<RecordData>();

        public Task<RecordResult<bool>> DeleteAsync(RecordRef record, long? expectedVersion, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api) =>
            Write<bool>();

        public Task<RecordResult<MergePreview>> PreviewMergeAsync(string entityKey, MergeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecordResult<MergeResult>> MergeAsync(string entityKey, MergeRequest request, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api) =>
            Write<MergeResult>();

        public Task<RecordResult<MergeLink>> ResolveMergedAsync(RecordRef record, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RecordResult<T>> RunAtomicAsync<T>(Func<CancellationToken, Task<RecordResult<T>>> work, CancellationToken cancellationToken) => Write<T>();

        private Task<RecordResult<T>> Write<T>()
        {
            Writes++;
            throw new InvalidOperationException("Önizleme kayıt yazmamalı.");
        }
    }
}
