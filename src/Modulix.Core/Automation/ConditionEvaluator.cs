using System.Globalization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Automation;

/// <summary>
/// Koşul ağacını bir kaydın değerlerine karşı değerlendirir ve her düğüm için nedenini açıklar (ADR-0027). Yan etkisizdir: yalnızca
/// <see cref="IAutomationFacts"/>'ı okur, hiçbir şey yazmaz; önizleme ve gerçek çalıştırma aynı kodu kullanır. Koşul önceden
/// <see cref="ConditionValidator"/> ile doğrulanmış olmalıdır; doğrulanmamış değer yanlış sayılır, istisna fırlatılmaz.
/// <list type="bullet">
///   <item>Null: karşılaştırmalar yanlış; <c>Ne</c> ve <c>NotIn</c> doğru; <c>IsEmpty</c>/<c>IsNotEmpty</c> açık.</item>
///   <item>Görülemeyen alan (alan yetkisi) ve bilinmeyen eski değer: yanlış; açıklama değeri göstermez.</item>
///   <item>Para: yalnızca aynı para birimiyle karşılaştırılır, farklı para birimi yanlıştır (dönüştürme yok).</item>
///   <item>Tarih: takvim günü tenant saat dilimindedir; an (DateTime) UTC'dir, gün işlemleri tenant gününe çevrilir.</item>
///   <item>Metin karşılaştırması büyük/küçük harf duyarsız ve sıra tabanlıdır (kültürden bağımsız).</item>
/// </list>
/// </summary>
public static class ConditionEvaluator
{
    public static async Task<ConditionExplanation> EvaluateAsync(
        ConditionNode? node, AutomationFieldSet fields, IAutomationFacts facts, TenantDay day, CancellationToken cancellationToken)
    {
        if (node is null)
        {
            return new ConditionExplanation(true, "Koşul yok: kural her kayıt için geçerli.", []);
        }

        return await EvaluateNodeAsync(node, fields, facts, day, cancellationToken);
    }

    private static async Task<ConditionExplanation> EvaluateNodeAsync(
        ConditionNode node, AutomationFieldSet fields, IAutomationFacts facts, TenantDay day, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case ConditionGroup group:
                var children = new List<ConditionExplanation>();
                foreach (var item in group.Items)
                {
                    children.Add(await EvaluateNodeAsync(item, fields, facts, day, cancellationToken));
                }

                var passed = group.Logic == ConditionLogic.And ? children.All(c => c.Passed) : children.Any(c => c.Passed);
                var joiner = group.Logic == ConditionLogic.And ? "Tümü sağlanmalı" : "Biri sağlanmalı";
                return new ConditionExplanation(passed, $"{joiner}: {(passed ? "sağlandı" : "sağlanmadı")}", children);

            case ConditionLeaf leaf:
                return EvaluateLeaf(leaf, fields, facts, day);

            case ConditionRelation relation:
                return await EvaluateRelationAsync(relation, fields, facts, cancellationToken);

            default:
                return new ConditionExplanation(false, "Bilinmeyen koşul düğümü: sağlanmadı", []);
        }
    }

    private static async Task<ConditionExplanation> EvaluateRelationAsync(
        ConditionRelation relation, AutomationFieldSet fields, IAutomationFacts facts, CancellationToken cancellationToken)
    {
        var description = ConditionText.Describe(relation, fields);
        var definition = fields.FindRelation(relation.Relation);
        if (definition is null || !fields.CanRead(definition))
        {
            return new ConditionExplanation(false, $"{description}: sağlanmadı (ilişki görülemiyor)", []);
        }

        var count = await facts.CountRelatedAsync(relation, cancellationToken);
        if (count is null)
        {
            return new ConditionExplanation(false, $"{description}: sağlanmadı (ilişki bu kayıt için sayılamadı)", []);
        }

        var passed = relation.Quantifier switch
        {
            RelationQuantifier.Exists => count > 0,
            RelationQuantifier.NotExists => count == 0,
            _ => CompareInt(count.Value, relation.CountOperator, relation.CountValue),
        };
        return new ConditionExplanation(passed, $"{description}: {(passed ? "sağlandı" : "sağlanmadı")} (bulunan: {count})", []);
    }

    private static ConditionExplanation EvaluateLeaf(ConditionLeaf leaf, AutomationFieldSet fields, IAutomationFacts facts, TenantDay day)
    {
        var description = ConditionText.Leaf(leaf, fields);
        var field = fields.Find(leaf.Field);
        if (field is null)
        {
            return Result(false, description, "bilinmeyen alan");
        }

        if (AutomationOperators.IsChange(leaf.Operator))
        {
            return EvaluateChange(leaf, field, facts, description);
        }

        var fact = facts.Get(field.Key, leaf.Source);
        switch (fact.State)
        {
            case AutomationFactState.Hidden:
                return Result(false, description, "alanı görme yetkisi yok");
            case AutomationFactState.Unavailable:
                return Result(false, description, leaf.Source == AutomationValueSource.Old ? "eski değer bilinmiyor (değişiklik bilgisi yok)" : "değer bilinmiyor");
            case AutomationFactState.Null:
                var nullResult = leaf.Operator is AutomationOperator.IsEmpty or AutomationOperator.Ne or AutomationOperator.NotIn;
                return Result(nullResult, description, "alan boş");
        }

        if (leaf.Operator == AutomationOperator.IsEmpty)
        {
            return Result(false, description, $"değer: {Display(field, fact.Value)}");
        }

        if (leaf.Operator == AutomationOperator.IsNotEmpty)
        {
            return Result(true, description, $"değer: {Display(field, fact.Value)}");
        }

        var (passed, note) = Compare(leaf, field, fact.Value!, day);
        return Result(passed, description, note ?? $"değer: {Display(field, fact.Value)}");
    }

    private static ConditionExplanation EvaluateChange(ConditionLeaf leaf, AutomationFieldDefinition field, IAutomationFacts facts, string description)
    {
        if (!facts.HasChangeInformation)
        {
            return Result(false, description, "değişiklik bilgisi yok");
        }

        var oldFact = facts.Get(field.Key, AutomationValueSource.Old);
        var newFact = facts.Get(field.Key, AutomationValueSource.New);
        if (oldFact.State is AutomationFactState.Hidden || newFact.State is AutomationFactState.Hidden)
        {
            return Result(false, description, "alanı görme yetkisi yok");
        }

        if (oldFact.State is AutomationFactState.Unavailable || newFact.State is AutomationFactState.Unavailable)
        {
            return Result(false, description, "değişiklik bilgisi yok");
        }

        var changed = !SameFact(oldFact, newFact);
        if (!changed)
        {
            return Result(false, description, "alan bu olayda değişmedi");
        }

        if (leaf.Operator == AutomationOperator.Changed)
        {
            return Result(true, description, "alan değişti");
        }

        var target = leaf.Operator == AutomationOperator.ChangedTo ? newFact : oldFact;
        if (target.State == AutomationFactState.Null)
        {
            return Result(false, description, "karşılaştırılan değer boş");
        }

        var equals = new ConditionLeaf(leaf.Field, AutomationOperator.Eq, leaf.Values) { Currency = leaf.Currency };
        var (passed, note) = Compare(equals, field, target.Value!, null);
        return Result(passed, description, note ?? (passed ? "değişiklik koşulu sağlandı" : "değişiklik değeri uyuşmuyor"));
    }

    private static bool SameFact(AutomationFact a, AutomationFact b) =>
        a.State == b.State && (a.State != AutomationFactState.Value || Equals(a.Value, b.Value));

    private static (bool Passed, string? Note) Compare(ConditionLeaf leaf, AutomationFieldDefinition field, object actual, TenantDay? day)
    {
        if (leaf.Operator is AutomationOperator.WithinNextDays or AutomationOperator.WithinLastDays or AutomationOperator.OlderThanDays)
        {
            return CompareDays(leaf, field, actual, day);
        }

        var expected = new List<object>();
        foreach (var text in leaf.Values)
        {
            if (!AutomationValues.TryParse(field.Type, field.Options, field.LookupKey, text, leaf.Currency, out var value, out _))
            {
                return (false, "koşuldaki değer geçersiz");
            }

            expected.Add(value!);
        }

        if (actual is AutomationMoney money
            && expected.OfType<AutomationMoney>().Any(m => !string.Equals(m.Currency, money.Currency, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"para birimi farklı ({money.Currency}); farklı para birimleri karşılaştırılmaz");
        }

        return leaf.Operator switch
        {
            AutomationOperator.Eq => (IsSame(actual, expected[0]), null),
            AutomationOperator.Ne => (!IsSame(actual, expected[0]), null),
            AutomationOperator.In => (expected.Any(e => IsSame(actual, e)), null),
            AutomationOperator.NotIn => (!expected.Any(e => IsSame(actual, e)), null),
            AutomationOperator.Contains => (actual is string text && text.Contains((string)expected[0], StringComparison.OrdinalIgnoreCase), null),
            AutomationOperator.StartsWith => (actual is string text && text.StartsWith((string)expected[0], StringComparison.OrdinalIgnoreCase), null),
            AutomationOperator.Gt or AutomationOperator.After => (Order(actual, expected[0]) is > 0, null),
            AutomationOperator.Gte => (Order(actual, expected[0]) is >= 0, null),
            AutomationOperator.Lt or AutomationOperator.Before => (Order(actual, expected[0]) is < 0, null),
            AutomationOperator.Lte => (Order(actual, expected[0]) is <= 0, null),
            AutomationOperator.Between => (Order(actual, expected[0]) is >= 0 && Order(actual, expected[1]) is <= 0, null),
            _ => (false, "işlem desteklenmiyor"),
        };
    }

    private static (bool Passed, string? Note) CompareDays(ConditionLeaf leaf, AutomationFieldDefinition field, object actual, TenantDay? day)
    {
        if (day is null || !AutomationValues.TryParseDays(leaf.Values[0], out var days))
        {
            return (false, "gün sayısı geçersiz");
        }

        var date = actual switch
        {
            DateOnly d => d,
            DateTimeOffset moment => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, day.Zone).DateTime),
            _ => (DateOnly?)null,
        };
        if (date is null)
        {
            return (false, "alan tarih değil");
        }

        var today = day.Today;
        return leaf.Operator switch
        {
            AutomationOperator.WithinNextDays => (date >= today && date <= today.AddDays(days), null),
            AutomationOperator.WithinLastDays => (date <= today && date >= today.AddDays(-days), null),
            _ => (date <= today.AddDays(-days), null),
        };
    }

    private static bool IsSame(object actual, object expected) => actual switch
    {
        string a => string.Equals(a, (string)expected, StringComparison.OrdinalIgnoreCase),
        AutomationMoney a => a.Amount == ((AutomationMoney)expected).Amount,
        _ => Equals(actual, expected),
    };

    private static int? Order(object actual, object expected) => (actual, expected) switch
    {
        (decimal a, decimal b) => a.CompareTo(b),
        (AutomationMoney a, AutomationMoney b) => a.Amount.CompareTo(b.Amount),
        (DateOnly a, DateOnly b) => a.CompareTo(b),
        (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),

        // Tarih alanı ile an karşılaştırması tanımsızdır; doğrulayıcı tip uyumunu zorlar.
        _ => null,
    };

    private static bool CompareInt(int count, AutomationOperator op, int value) => op switch
    {
        AutomationOperator.Eq => count == value,
        AutomationOperator.Ne => count != value,
        AutomationOperator.Gt => count > value,
        AutomationOperator.Gte => count >= value,
        AutomationOperator.Lt => count < value,
        AutomationOperator.Lte => count <= value,
        _ => false,
    };

    private static ConditionExplanation Result(bool passed, string description, string note) =>
        new(passed, $"{description}: {(passed ? "sağlandı" : "sağlanmadı")} ({note})", []);

    private static string Display(AutomationFieldDefinition field, object? value) => value switch
    {
        string text when field.Type == AutomationFieldType.Choice => field.Options.FirstOrDefault(o => o.Key == text)?.Label ?? text,
        decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
        AutomationMoney money => $"{money.Amount.ToString("0.##", CultureInfo.InvariantCulture)} {money.Currency}",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
        bool flag => flag ? "evet" : "hayır",
        Guid id => id.ToString(),
        null => "boş",
        _ => value.ToString() ?? string.Empty,
    };
}
