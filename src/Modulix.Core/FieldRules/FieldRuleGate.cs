using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;

namespace Modulix.Core.FieldRules;

/// <summary>
/// Alan kurallarının kayıt boru hattındaki kapısı (ADR-0029 K7). Boru hattı kaydı yazmadan önce, yazılacak son halini buna sorar;
/// kapı firmanın etkin kurallarını (<see cref="ITenantFieldRules"/>) o kayıt türüne süzer ve ADR-0027'nin koşul değerlendiricisiyle
/// değerlendirir. Kural yetkiyi genişletmez; yalnızca yazmayı daraltır.
/// </summary>
public interface IFieldRuleGate
{
    /// <param name="before">Güncellemede kaydın mevcut hali; oluşturmada <c>null</c>.</param>
    /// <param name="after">Yazılacak son hal (güncellemede mevcut değerlerle birleşmiş).</param>
    Task<IReadOnlyList<RecordFieldError>> CheckAsync(
        string entityKey, Guid? recordId, JsonObject? before, JsonObject after, CancellationToken cancellationToken);
}

/// <summary>
/// Sunucu tarafı uygulama: zorunlu alan boşsa, doğrulama ve engel kuralı sağlanıyorsa, salt okunur alan değiştirilmişse yazma reddedilir.
/// Onay gerektiren kural da reddeder (onay akışı henüz yoktur; sessizce geçirmek yanlış olurdu). Uyarılar yazmayı engellemez ve
/// bu sürümde arayüze taşınmaz. Gizli alan yazmayı reddettirmez ve değeri silinmez. Kuralı olmayan kayıt türünde (ör. CRM) veritabanına
/// gidilmez. Firmanın etkin kuralları ve alan kümesi istek boyunca saklanır.
/// </summary>
public sealed class FieldRuleGate(ModuleCatalog modules, IServiceProvider services) : IFieldRuleGate
{
    private IReadOnlyList<FieldRule>? effective;

    public async Task<IReadOnlyList<RecordFieldError>> CheckAsync(
        string entityKey, Guid? recordId, JsonObject? before, JsonObject after, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationEntity(entityKey) is null)
        {
            return [];
        }

        effective ??= (await services.GetRequiredService<ITenantFieldRules>().GetEffectiveAsync(cancellationToken)).Rules;
        var forEntity = effective.Where(r => FieldRuleChecker.EntityOf(modules, r.Field) == entityKey).ToList();
        if (forEntity.Count == 0 || await services.GetRequiredService<IAutomationCatalog>().GetFieldSetAsync(entityKey, cancellationToken) is not { } fields)
        {
            return [];
        }

        var relations = services.GetRequiredService<IAutomationRelationCounter>();
        var day = await services.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
        var id = recordId ?? Guid.Empty;
        var changes = before is null ? null : RecordDiff.Compute(RecordCustomFields.Flatten(before), RecordCustomFields.Flatten(after));
        var facts = new RecordFacts(fields, id, after, changes, relations);
        var result = await FieldRuleEvaluator.EvaluateAsync(forEntity, fields, facts, day, cancellationToken);

        var errors = new List<RecordFieldError>();
        foreach (var state in result.Fields.Values)
        {
            var name = fields.Find(state.Field)?.Name ?? state.Field;
            if (state.Required && facts.Get(state.Field, AutomationValueSource.New).State == AutomationFactState.Null)
            {
                errors.Add(new RecordFieldError(name, "Bu alan, kayıt bu durumdayken zorunludur."));
            }

            errors.AddRange(state.Errors.Select(m => new RecordFieldError(name, m)));
            errors.AddRange(state.Blocks.Select(m => new RecordFieldError(name, m)));
            errors.AddRange(state.Approvals.Select(m => new RecordFieldError(name, "Onay gerekir (onay akışı henüz yok): " + m)));
        }

        if (before is not null)
        {
            errors.AddRange(await ReadOnlyViolationsAsync(forEntity, fields, relations, id, before, after, day, cancellationToken));
        }

        return errors;
    }

    // Salt okunurluk kaydın mevcut haline göre belirlenir: koşulu aynı istekte değiştirip alanı açmak kuralı aşmanın yolu olmasın.
    private async Task<IEnumerable<RecordFieldError>> ReadOnlyViolationsAsync(
        List<FieldRule> forEntity, AutomationFieldSet fields, IAutomationRelationCounter relations, Guid id, JsonObject before, JsonObject after, TenantDay day, CancellationToken cancellationToken)
    {
        var readOnlyRules = forEntity.Where(r => r.Effect == FieldRuleEffect.ReadOnly).ToList();
        if (readOnlyRules.Count == 0)
        {
            return [];
        }

        var facts = new RecordFacts(fields, id, before, null, relations);
        var current = await FieldRuleEvaluator.EvaluateAsync(readOnlyRules, fields, facts, day, cancellationToken);
        var errors = new List<RecordFieldError>();
        foreach (var state in current.Fields.Values.Where(s => s.ReadOnly))
        {
            var name = fields.Find(state.Field)?.Name ?? state.Field;
            if (Changed(before, after, name))
            {
                errors.Add(new RecordFieldError(name, "Bu alan şu an salt okunur; değiştirilemez."));
            }
        }

        return errors;
    }

    // "custom.x" gibi yollar iç içe nesnede okunur (RecordFacts ile aynı).
    private static bool Changed(JsonObject before, JsonObject after, string path) =>
        !JsonNode.DeepEquals(Read(before, path), Read(after, path));

    private static JsonNode? Read(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }
}
