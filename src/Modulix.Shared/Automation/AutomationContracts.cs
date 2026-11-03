using System.Text.Json;

namespace Modulix.Shared.Automation;

// Otomasyon kuralları (/api/automation, ADR-0027, Dilim 1). Kural tanımı (tetikleyici, koşul, aksiyonlar) JSON olarak gidip gelir;
// biçimi sunucuda doğrulanır. Kurallar bu sürümde yönetilir ve önizlenir, henüz çalıştırılmaz.

public sealed record AutomationIssueDto(string Path, string Message);

public sealed record AutomationRuleSummaryDto(
    Guid Id,
    string Name,
    string ModuleKey,
    string EntityKey,
    string Status,
    int? PublishedVersionNo,
    bool HasDraft,
    int Priority,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record AutomationRulePageDto(IReadOnlyList<AutomationRuleSummaryDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Kural sürümü. <see cref="Summary"/> şablondan üretilmiş okunabilir özettir.</summary>
public sealed record AutomationVersionDto(
    Guid Id,
    int VersionNo,
    string State,
    string Name,
    string? Description,
    int Priority,
    bool StopProcessing,
    DateTimeOffset? ActiveFrom,
    DateTimeOffset? ActiveUntil,
    JsonElement Definition,
    string Hash,
    string? Summary,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy);

/// <summary>Kural ayrıntısı. Sorun listeleri sürümlerin şu anki katalogla doğrulanma sonucudur (silinen alan/pasif modül burada görünür).</summary>
public sealed record AutomationRuleDetailDto(
    AutomationRuleSummaryDto Rule,
    AutomationVersionDto? Draft,
    AutomationVersionDto? Published,
    IReadOnlyList<AutomationIssueDto> DraftIssues,
    IReadOnlyList<AutomationIssueDto> PublishedIssues);

public sealed record CreateAutomationRuleRequest(
    string ModuleKey,
    string EntityKey,
    string Name,
    string? Description,
    int Priority,
    bool StopProcessing,
    DateTimeOffset? ActiveFrom,
    DateTimeOffset? ActiveUntil,
    JsonElement Definition);

public sealed record UpdateAutomationRuleRequest(
    string Name,
    string? Description,
    int Priority,
    bool StopProcessing,
    DateTimeOffset? ActiveFrom,
    DateTimeOffset? ActiveUntil,
    JsonElement Definition,
    long? ExpectedVersion);

public sealed record PublishAutomationRuleRequest(bool Activate, long? ExpectedVersion);

public sealed record SetAutomationRuleActiveRequest(bool IsActive);

/// <summary>Yan etkisiz önizleme: kayıt verilmezse yalnızca tanım doğrulanır. <see cref="Changes"/> olayın <c>{alan:{old,new}}</c> farkı (isteğe bağlı).</summary>
public sealed record PreviewAutomationRequest(string ModuleKey, string EntityKey, JsonElement Definition, Guid? RecordId, JsonElement? Changes);

public sealed record ConditionExplanationDto(bool Passed, string Text, IReadOnlyList<ConditionExplanationDto> Children);

public sealed record PlannedActionDto(
    string StepId, string Action, string DisplayName, string SideEffect, JsonElement Parameters, bool Permitted, string? Note);

public sealed record AutomationPreviewDto(
    IReadOnlyList<AutomationIssueDto> Issues,
    string TriggerText,
    string ConditionText,
    bool Evaluated,
    bool ConditionsPassed,
    ConditionExplanationDto? Explanation,
    IReadOnlyList<PlannedActionDto> PlannedActions,
    IReadOnlyList<string> Warnings);

public sealed record AutomationOptionDto(string Key, string Label);

public sealed record AutomationFieldDto(
    string Key,
    string Label,
    string Type,
    IReadOnlyList<string> Operators,
    IReadOnlyList<AutomationOptionDto> Options,
    string? LookupKey,
    string? Hint);

public sealed record AutomationEventDto(string Key, string EventType, string DisplayName);

public sealed record AutomationRelationDto(string Key, string Label, string TargetEntityKey, IReadOnlyList<AutomationFieldDto> TargetFields);

public sealed record AutomationEntityDto(
    string ModuleKey,
    string EntityKey,
    string DisplayName,
    IReadOnlyList<AutomationFieldDto> Fields,
    IReadOnlyList<AutomationEventDto> Events,
    IReadOnlyList<AutomationRelationDto> Relations);

public sealed record AutomationParameterDto(
    string Name, string Label, string Type, bool Required, string? Description, IReadOnlyList<AutomationOptionDto> Options, string? LookupKey);

public sealed record AutomationActionDto(
    string Key,
    string ModuleKey,
    string DisplayName,
    string Description,
    string SideEffect,
    bool SupportsDryRun,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<AutomationParameterDto> Parameters);

public sealed record AutomationCatalogDto(IReadOnlyList<AutomationEntityDto> Entities, IReadOnlyList<AutomationActionDto> Actions);

// Çalışma geçmişi (ADR-0027, Dilim 2). Kayıt değeri ve parametre içermez; yalnızca durum, özet ve kimlikler.
public sealed record AutomationRunDto(
    Guid Id,
    Guid RuleId,
    string RuleName,
    int VersionNo,
    string EntityKey,
    Guid RecordId,
    string Origin,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Note);

public sealed record AutomationRunPageDto(IReadOnlyList<AutomationRunDto> Items, int Page, int PageSize, int TotalCount);

public sealed record AutomationRunStepDto(
    int StepNo,
    string StepId,
    string Action,
    string Status,
    int Attempts,
    string? Summary,
    string? EffectEntityKey,
    Guid? EffectRecordId,
    DateTimeOffset? CompletedAt);

public sealed record AutomationRunDetailDto(AutomationRunDto Run, DateTimeOffset NextAttemptAt, IReadOnlyList<AutomationRunStepDto> Steps);

public sealed record RunAutomationRuleRequest(Guid RecordId);
