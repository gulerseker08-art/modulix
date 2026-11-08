using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>Aksiyon parametresinin kayıttan bağlandığı alan: <c>{"$field": "anahtar"}</c>.</summary>
public static class ActionParameterBinding
{
    public const string FieldProperty = "$field";

    public static string? FieldOf(JsonNode? value) =>
        value is JsonObject obj && obj.Count == 1 && obj[FieldProperty] is JsonValue field && field.TryGetValue<string>(out var key) ? key : null;
}

/// <summary>
/// Kural tanımının tamamını katalogla ve çağıranın yetkisiyle doğrular (ADR-0027): modül ve kayıt türü, tetikleyici, koşul, aksiyonlar
/// (şema, izin, modül/lisans). Yayın ve önizleme bunu kullanır. Yayınlayan, sahip olmadığı izni gerektiren aksiyonu ya da göremediği
/// alanı kullanamaz (yetki yükseltme yok).
/// </summary>
public sealed partial class AutomationDefinitionValidator(
    ModuleCatalog modules, IAutomationCatalog catalog, ICurrentAccess access, IEnumerable<IAutomationStepCheck>? stepChecks = null)
{
    public const int MaxActions = 20;

    public async Task<IReadOnlyList<AutomationIssue>> ValidateAsync(
        string moduleKey, string entityKey, AutomationDefinition definition, CancellationToken cancellationToken)
    {
        var issues = new List<AutomationIssue>();
        if (modules.FindAutomationEntity(entityKey) is not { } found || found.ModuleKey != moduleKey)
        {
            issues.Add(new("entity", "Kayıt türü bu modülde kural yazmaya açık değil."));
            return issues;
        }

        if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
        {
            issues.Add(new("module", $"'{modules.Find(moduleKey)?.DisplayName ?? moduleKey}' modülü bu firmada etkin değil."));
            return issues;
        }

        var fields = await catalog.GetFieldSetAsync(entityKey, cancellationToken);
        if (fields is null)
        {
            issues.Add(new("entity", "Bu kayıt türüne kural yazma izniniz yok."));
            return issues;
        }

        ValidateTrigger(definition.Trigger, entityKey, fields, issues);
        issues.AddRange(ConditionValidator.Validate(definition.Condition, fields, OptionsFor(definition.Trigger)));
        await ValidateActionsAsync(entityKey, definition.Actions, fields, issues, cancellationToken);
        return issues;
    }

    /// <summary>Tetikleyiciye göre koşulda eski değer ve değişiklik operatörleri: yalnızca kayıt güncelleme tetikleyicilerinde.</summary>
    public static ConditionOptions OptionsFor(AutomationTrigger trigger) =>
        trigger is FieldChangedTrigger or StatusTransitionTrigger ? ConditionOptions.WithChanges : ConditionOptions.Current;

    private void ValidateTrigger(AutomationTrigger trigger, string entityKey, AutomationFieldSet fields, List<AutomationIssue> issues)
    {
        var events = modules.AutomationEventsOf(entityKey).ToList();
        bool Has(string eventType) => events.Any(e => e.EventType == eventType);
        AutomationFieldDefinition? Field(string key, string path)
        {
            var field = fields.Find(key);
            if (field is null)
            {
                issues.Add(new(path, $"Bilinmeyen alan: '{key}'."));
            }
            else if (!fields.CanRead(field))
            {
                issues.Add(new(path, $"'{field.Label}' alanını görme izniniz yok."));
            }

            return field;
        }

        // Tarama tabanlı tetikleyiciler kayıtları modülün kayıt kaynağından alır; kaynağı olmayan kayıt türünde sessizce hiç tetiklenmezdi.
        if (trigger is ScheduledTrigger or DateRelativeTrigger or InactivityTrigger
            && modules.FindAutomationEntity(entityKey)?.Entity.RecordSource is null)
        {
            issues.Add(new("trigger", "Bu kayıt türü zamanlanmış ve tarihe bağlı tetikleyicileri desteklemiyor."));
        }

        switch (trigger)
        {
            case RecordCreatedTrigger:
                if (!Has(DomainEventTypes.RecordCreated))
                {
                    issues.Add(new("trigger", "Bu kayıt türü için 'kayıt oluşturulunca' tetikleyicisi tanımlı değil."));
                }

                break;

            case FieldChangedTrigger changed:
                if (!Has(DomainEventTypes.RecordUpdated))
                {
                    issues.Add(new("trigger", "Bu kayıt türü için 'alan değişince' tetikleyicisi tanımlı değil."));
                }

                if (changed.Fields.Count is 0 or > 20)
                {
                    issues.Add(new("trigger.fields", "1-20 alan seçin."));
                }

                foreach (var key in changed.Fields)
                {
                    Field(key, "trigger.fields");
                }

                break;

            case StatusTransitionTrigger transition:
                if (!Has(DomainEventTypes.RecordUpdated))
                {
                    issues.Add(new("trigger", "Bu kayıt türü için durum geçişi tetikleyicisi tanımlı değil."));
                }

                if (Field(transition.Field, "trigger.field") is { } statusField)
                {
                    if (statusField.Type != AutomationFieldType.Choice)
                    {
                        issues.Add(new("trigger.field", "Durum geçişi yalnızca seçim alanlarında kullanılabilir."));
                    }
                    else if (statusField.Options.Count > 0
                        && (statusField.Options.All(o => o.Key != transition.To)
                            || (transition.From is not null && statusField.Options.All(o => o.Key != transition.From))))
                    {
                        issues.Add(new("trigger", "Geçiş değerleri alanın seçeneklerinden olmalı."));
                    }
                }

                break;

            case ScheduledTrigger scheduled:
                if (!TimeOfDayPattern().IsMatch(scheduled.TimeOfDay))
                {
                    issues.Add(new("trigger.timeOfDay", "Saat 'SS:dd' biçiminde olmalı (ör. 06:00)."));
                }

                break;

            case DateRelativeTrigger relative:
                if (Field(relative.Field, "trigger.field") is { } dateField
                    && dateField.Type is not (AutomationFieldType.Date or AutomationFieldType.DateTime))
                {
                    issues.Add(new("trigger.field", "Yalnızca tarih alanı seçilebilir."));
                }

                if (Math.Abs(relative.OffsetDays) > AutomationValues.MaxDays)
                {
                    issues.Add(new("trigger.offsetDays", $"Gün farkı en çok {AutomationValues.MaxDays} olabilir."));
                }

                break;

            case InactivityTrigger inactivity:
                if (fields.FindRelation(inactivity.Relation) is not { } relation)
                {
                    issues.Add(new("trigger.relation", $"Bilinmeyen ilişki: '{inactivity.Relation}'."));
                }
                else if (!fields.CanRead(relation))
                {
                    issues.Add(new("trigger.relation", $"'{relation.Label}' ilişkisini görme izniniz yok."));
                }

                if (inactivity.Days is < 1 or > AutomationValues.MaxDays)
                {
                    issues.Add(new("trigger.days", $"Gün sayısı 1-{AutomationValues.MaxDays} arasında olmalı."));
                }

                break;

            case DomainEventTrigger domainEvent:
                var declared = events.FirstOrDefault(e => e.Key == domainEvent.EventKey);
                if (declared is null || declared.EventType is DomainEventTypes.RecordCreated or DomainEventTypes.RecordUpdated)
                {
                    issues.Add(new("trigger.eventKey", $"Bu kayıt türü için tanımlı bir iş olayı değil: '{domainEvent.EventKey}'."));
                }

                break;

            case ManualTrigger:
                break;

            default:
                issues.Add(new("trigger", "Bilinmeyen tetikleyici."));
                break;
        }
    }

    private async Task ValidateActionsAsync(
        string entityKey, IReadOnlyList<AutomationActionStep> actions, AutomationFieldSet fields, List<AutomationIssue> issues, CancellationToken cancellationToken)
    {
        if (actions.Count == 0)
        {
            issues.Add(new("actions", "En az bir aksiyon ekleyin."));
            return;
        }

        if (actions.Count > MaxActions)
        {
            issues.Add(new("actions", $"En çok {MaxActions} aksiyon eklenebilir."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < actions.Count; i++)
        {
            var step = actions[i];
            var path = $"actions[{i}]";
            if (!StepIdPattern().IsMatch(step.Id) || !ids.Add(step.Id))
            {
                issues.Add(new(path, "Adım kimliği küçük harf, rakam ve tire içermeli (en çok 40 karakter) ve kuralda tekil olmalı."));
            }

            var view = await catalog.FindActionAsync(step.Action, cancellationToken);
            if (view is null)
            {
                issues.Add(new(path, $"Bilinmeyen ya da bu firmada kapalı aksiyon: '{step.Action}'."));
                continue;
            }

            if (!view.Available)
            {
                issues.Add(new(path, $"'{view.Definition.DisplayName}': {view.UnavailableReason}"));
                continue;
            }

            ValidateParameters(view.Definition, step.Parameters ?? new JsonObject(), fields, path, issues);

            // Aksiyona özgü denetim: parametrenin tenant verisine karşı geçerliliği (ör. onay süreci adı) - Dilim 4c-2.
            foreach (var check in stepChecks?.Where(c => c.ActionKey == step.Action) ?? [])
            {
                issues.AddRange(await check.CheckAsync(entityKey, step, path, cancellationToken));
            }
        }
    }

    /// <summary>Aksiyon parametrelerini şemaya göre doğrular: bilinmeyen/eksik parametre, tip, seçenek, alan bağlama.</summary>
    public static void ValidateParameters(
        AutomationActionDefinition definition, JsonObject parameters, AutomationFieldSet fields, string path, List<AutomationIssue> issues)
    {
        var known = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var name in parameters.Select(p => p.Key).Where(k => !known.ContainsKey(k)))
        {
            issues.Add(new($"{path}.parameters.{name}", $"'{definition.DisplayName}' aksiyonunda '{name}' parametresi yok."));
        }

        foreach (var parameter in definition.Parameters)
        {
            var parameterPath = $"{path}.parameters.{parameter.Name}";
            parameters.TryGetPropertyValue(parameter.Name, out var value);
            if (value is null)
            {
                if (parameter.Required)
                {
                    issues.Add(new(parameterPath, $"'{parameter.Label}' zorunlu."));
                }

                continue;
            }

            if (ActionParameterBinding.FieldOf(value) is { } fieldKey)
            {
                ValidateBinding(parameter, fieldKey, fields, parameterPath, issues);
                continue;
            }

            if (!TryLiteral(parameter, value, out var parsed, out var error))
            {
                issues.Add(new(parameterPath, $"'{parameter.Label}': {error}"));
            }
            else if (parameter.Type == AutomationFieldType.Text && parsed is string text)
            {
                ValidateTemplate(parameter, text, fields, parameterPath, issues);
            }
        }
    }

    /// <summary>Metin parametresindeki <c>{{alan}}</c> değişkenleri (Dilim 4d): biçim, beyaz liste, alan yetkisi, tip.</summary>
    private static void ValidateTemplate(AutomationParameter parameter, string text, AutomationFieldSet fields, string path, List<AutomationIssue> issues)
    {
        foreach (var error in AutomationTextTemplate.Errors(text))
        {
            issues.Add(new(path, $"'{parameter.Label}': {error}"));
        }

        var variables = AutomationTextTemplate.Variables(text);
        if (variables.Count == 0)
        {
            return;
        }

        if (!parameter.AllowFieldBinding)
        {
            issues.Add(new(path, $"'{parameter.Label}' kayıt değişkeni ({{{{alan}}}}) kabul etmez; metni doğrudan yazın."));
            return;
        }

        foreach (var variable in variables)
        {
            if (AutomationTextTemplate.CheckVariable(fields, variable) is { } problem)
            {
                issues.Add(new(path, $"'{parameter.Label}': {problem}"));
            }
        }
    }

    private static void ValidateBinding(AutomationParameter parameter, string fieldKey, AutomationFieldSet fields, string path, List<AutomationIssue> issues)
    {
        var field = fields.Find(fieldKey);
        if (!parameter.AllowFieldBinding)
        {
            issues.Add(new(path, $"'{parameter.Label}' kayıttan bağlanamaz; değeri doğrudan verin."));
        }
        else if (field is null)
        {
            issues.Add(new(path, $"Bağlanan alan bilinmiyor: '{fieldKey}'."));
        }
        else if (!fields.CanRead(field))
        {
            issues.Add(new(path, $"'{field.Label}' alanını görme izniniz yok; parametreye bağlanamaz."));
        }
        else if (field.Type != parameter.Type || field.LookupKey != parameter.LookupKey)
        {
            issues.Add(new(path, $"'{field.Label}' alanının tipi '{parameter.Label}' parametresiyle uyuşmuyor."));
        }
    }

    /// <summary>Sabit parametre değerini tipine çözer (JSON değeri → tipli değer). Hata iletisi kullanıcıya gösterilir.</summary>
    public static bool TryLiteral(AutomationParameter parameter, JsonNode value, out object? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (value is not JsonValue json)
        {
            error = "değer sade bir değer olmalı.";
            return false;
        }

        string text;
        switch (json.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.String:
                text = json.GetValue<string>();
                break;
            case System.Text.Json.JsonValueKind.True:
                text = "true";
                break;
            case System.Text.Json.JsonValueKind.False:
                text = "false";
                break;
            case System.Text.Json.JsonValueKind.Number:
                text = json.ToJsonString();
                break;
            default:
                error = "değer metin, sayı ya da evet/hayır olmalı.";
                return false;
        }

        if (parameter.Type == AutomationFieldType.Text)
        {
            if (text.Length == 0 || text.Length > parameter.MaxLength)
            {
                error = $"1-{parameter.MaxLength} karakter olmalı.";
                return false;
            }

            parsed = text;
            return true;
        }

        return AutomationValues.TryParse(parameter.Type, parameter.Options, parameter.LookupKey, text, null, out parsed, out error);
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOfDayPattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();
}
