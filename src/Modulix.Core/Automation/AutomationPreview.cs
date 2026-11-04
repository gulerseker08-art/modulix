using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>Önizlemede planlanan aksiyon: çalıştırılmaz, yalnızca parametreleri çözülür ve izin durumu gösterilir.</summary>
public sealed record PlannedAction(
    string StepId,
    string Action,
    string DisplayName,
    AutomationSideEffect SideEffect,
    JsonObject Parameters,
    bool Permitted,
    string? Note);

/// <summary>
/// Yan etkisiz önizleme sonucu (ADR-0027). <see cref="Issues"/> doluysa tanım geçersizdir ve değerlendirme yapılmaz.
/// <see cref="Evaluated"/> yalnızca örnek kayıt verilip değerlendirildiğinde doludur. <see cref="Explanation"/> hangi koşulların
/// geçtiğini düğüm düğüm gösterir; <see cref="PlannedActions"/> yalnızca koşul geçtiyse dolu. Şablondan üretilmiş metindir.
/// </summary>
public sealed record AutomationPreview(
    IReadOnlyList<AutomationIssue> Issues,
    string TriggerText,
    string ConditionText,
    bool Evaluated,
    bool ConditionsPassed,
    ConditionExplanation? Explanation,
    IReadOnlyList<PlannedAction> PlannedActions,
    IReadOnlyList<string> Warnings);

public sealed record AutomationPreviewRequest(
    string ModuleKey, string EntityKey, AutomationDefinition Definition, Guid? RecordId, JsonObject? Changes);

/// <summary>
/// Kuralı yayınlamadan örnek kayıt üzerinde dener: hangi koşulların geçtiğini ve hangi aksiyonların planlandığını gösterir.
/// <b>Hiçbir yan etki üretmez</b>: aksiyon işleyicisi çağrılmaz, kayıt yazılmaz, denetim kaydı ve olay oluşmaz, bildirim gitmez.
/// Örnek kayıt kayıt boru hattından okunur (çağıranın izni, veri kapsamı ve hassas alan kuralları uygulanır).
/// </summary>
public interface IAutomationPreview
{
    Task<RecordResult<AutomationPreview>> PreviewAsync(AutomationPreviewRequest request, CancellationToken cancellationToken);
}

public sealed class AutomationPreviewService(
    AutomationDefinitionValidator validator,
    IAutomationCatalog catalog,
    IRecordPipeline pipeline,
    ITenantClock clock,
    IAutomationRelationCounter relations,
    ModuleCatalog modules) : IAutomationPreview
{
    public async Task<RecordResult<AutomationPreview>> PreviewAsync(AutomationPreviewRequest request, CancellationToken cancellationToken)
    {
        var fields = await catalog.GetFieldSetAsync(request.EntityKey, cancellationToken);
        if (fields is null || modules.FindAutomationEntity(request.EntityKey) is not { } entity || entity.ModuleKey != request.ModuleKey)
        {
            return RecordResult<AutomationPreview>.Fail(RecordErrorCode.UnknownEntity, "Kayıt türü kural yazmaya açık değil ya da erişiminiz yok.");
        }

        var issues = await validator.ValidateAsync(request.ModuleKey, request.EntityKey, request.Definition, cancellationToken);
        var triggerText = ConditionText.Trigger(request.Definition.Trigger, fields);
        var conditionText = ConditionText.Describe(request.Definition.Condition, fields);
        if (issues.Count > 0)
        {
            return RecordResult<AutomationPreview>.Ok(new AutomationPreview(issues, triggerText, conditionText, false, false, null, [], []));
        }

        var warnings = new List<string>();
        if (request.RecordId is null)
        {
            warnings.Add("Örnek kayıt seçilmedi: koşullar değerlendirilmedi.");
            return RecordResult<AutomationPreview>.Ok(new AutomationPreview([], triggerText, conditionText, false, false, null, [], warnings));
        }

        var record = await pipeline.GetAsync(new RecordRef(request.EntityKey, request.RecordId.Value), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<AutomationPreview>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var usesChanges = ConditionUsesChanges(request.Definition);
        if (usesChanges && request.Changes is null)
        {
            warnings.Add("Değişiklik bilgisi verilmedi: eski değer ve değişiklik koşulları sağlanmaz.");
        }

        var facts = new RecordFacts(fields, record.Value!.Id, record.Value.Values, request.Changes, relations);
        var day = await clock.GetAsync(cancellationToken);
        var explanation = await ConditionEvaluator.EvaluateAsync(request.Definition.Condition, fields, facts, day, cancellationToken);
        var planned = explanation.Passed
            ? await PlanAsync(request.Definition.Actions, fields, facts, cancellationToken)
            : [];
        return RecordResult<AutomationPreview>.Ok(
            new AutomationPreview([], triggerText, conditionText, true, explanation.Passed, explanation, planned, warnings));
    }

    private static bool ConditionUsesChanges(AutomationDefinition definition) =>
        definition.Trigger is FieldChangedTrigger or StatusTransitionTrigger && Walk(definition.Condition);

    private static bool Walk(ConditionNode? node) => node switch
    {
        ConditionLeaf leaf => leaf.Source == AutomationValueSource.Old || AutomationOperators.IsChange(leaf.Operator),
        ConditionGroup group => group.Items.Any(Walk),
        _ => false,
    };

    // Parametreler çözülür; aksiyon işleyicisi çağrılmaz (Dilim 3'te SupportsDryRun işleyicileri için ayrıca eklenir).
    private async Task<IReadOnlyList<PlannedAction>> PlanAsync(
        IReadOnlyList<AutomationActionStep> steps, AutomationFieldSet fields, IAutomationFacts facts, CancellationToken cancellationToken)
    {
        var planned = new List<PlannedAction>();
        foreach (var step in steps)
        {
            var view = await catalog.FindActionAsync(step.Action, cancellationToken);
            if (view is null)
            {
                continue;
            }

            var resolved = AutomationParameterResolver.Resolve(step, fields, facts);
            var parameters = resolved.Values;
            var notes = resolved.Notes.ToList();

            var effect = view.Definition.SideEffect switch
            {
                AutomationSideEffect.Read => "okuma",
                AutomationSideEffect.Draft => "taslak oluşturur",
                _ => "kayıt değiştirir ya da dış etki üretir",
            };
            notes.Add($"Planlandı, çalıştırılmadı ({effect}).");
            planned.Add(new PlannedAction(
                step.Id, step.Action, view.Definition.DisplayName, view.Definition.SideEffect, parameters, view.Available,
                string.Join(" ", view.Available ? notes : notes.Prepend(view.UnavailableReason!))));
        }

        return planned;
    }
}

/// <summary>Kural sürümünün okunabilir özeti ("Şu olunca / koşul / aksiyonlar"); denetim kaydında ve ekranda kullanılır.</summary>
public static class AutomationSummary
{
    public static string Describe(AutomationDefinition definition, AutomationFieldSet fields, ModuleCatalog modules)
    {
        var actions = definition.Actions.Select(a =>
            modules.FindAutomationAction(a.Action)?.Action.Definition.DisplayName ?? a.Action);
        return $"{ConditionText.Trigger(definition.Trigger, fields)} | {ConditionText.Describe(definition.Condition, fields)} | {string.Join(", ", actions)}";
    }
}
