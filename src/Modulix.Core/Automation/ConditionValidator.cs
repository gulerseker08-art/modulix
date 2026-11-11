using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>
/// Hangi koşul özellikleri kullanılabilir: <see cref="AllowOld"/> eski değer (olayın farkı varsa), <see cref="AllowChange"/>
/// <c>Changed</c> operatörleri. Tetikleyiciye göre belirlenir (yalnızca kayıt güncelleme olayında fark vardır).
/// </summary>
public readonly record struct ConditionOptions(bool AllowOld, bool AllowChange)
{
    public static ConditionOptions Current { get; } = new(false, false);

    public static ConditionOptions WithChanges { get; } = new(true, true);

    /// <summary>Adım koşulu (dallanma, Dilim 4c-5): güncel kayda bakar; eski değer ve değişiklik yok, iletiler adıma göre yazılır.</summary>
    public static ConditionOptions StepCondition { get; } = new(false, false) { Step = true };

    public bool Step { get; init; }
}

/// <summary>Koşul ağacının yapısal ve tip doğrulaması (saf; veritabanı bilmez). Sorunların hepsini toplar.</summary>
public static class ConditionValidator
{
    public const int MaxDepth = 4;

    public const int MaxLeaves = 50;

    public const int MaxGroupItems = 20;

    private static readonly AutomationOperator[] CountOperators =
        [AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.Gt, AutomationOperator.Gte, AutomationOperator.Lt, AutomationOperator.Lte];

    /// <param name="path">Sorunların yolu: kural koşulu için <c>condition</c>, adım koşulu için <c>actions[i].when</c>.</param>
    public static IReadOnlyList<AutomationIssue> Validate(
        ConditionNode? node, AutomationFieldSet fields, ConditionOptions options, string path = "condition")
    {
        var issues = new List<AutomationIssue>();
        if (node is null)
        {
            return issues;
        }

        var leaves = 0;
        Walk(node, fields, options, path, depth: 1, allowRelation: true, issues, ref leaves);
        if (leaves > MaxLeaves)
        {
            issues.Add(new(path, $"Koşul en çok {MaxLeaves} satır içerebilir."));
        }

        return issues;
    }

    private static void Walk(
        ConditionNode node, AutomationFieldSet fields, ConditionOptions options, string path, int depth, bool allowRelation,
        List<AutomationIssue> issues, ref int leaves)
    {
        switch (node)
        {
            case ConditionGroup group:
                if (depth > MaxDepth)
                {
                    issues.Add(new(path, $"Koşul grupları en çok {MaxDepth} seviye iç içe olabilir."));
                    return;
                }

                if (group.Items.Count == 0)
                {
                    issues.Add(new(path, "Boş koşul grubu geçersiz; en az bir koşul ekleyin ya da grubu kaldırın."));
                }

                if (group.Items.Count > MaxGroupItems)
                {
                    issues.Add(new(path, $"Bir grupta en çok {MaxGroupItems} koşul olabilir."));
                }

                for (var i = 0; i < group.Items.Count; i++)
                {
                    Walk(group.Items[i], fields, options, $"{path}.items[{i}]", depth + 1, allowRelation, issues, ref leaves);
                }

                break;

            case ConditionLeaf leaf:
                leaves++;
                ValidateLeaf(leaf, fields, options, path, issues);
                break;

            case ConditionRelation relation:
                leaves++;
                ValidateRelation(relation, fields, options, path, allowRelation, issues, ref leaves);
                break;

            default:
                issues.Add(new(path, "Bilinmeyen koşul düğümü."));
                break;
        }
    }

    private static void ValidateRelation(
        ConditionRelation relation, AutomationFieldSet fields, ConditionOptions options, string path, bool allowRelation,
        List<AutomationIssue> issues, ref int leaves)
    {
        if (!allowRelation)
        {
            issues.Add(new(path, "İlişki koşulunun içinde ikinci bir ilişki kullanılamaz."));
            return;
        }

        var definition = fields.FindRelation(relation.Relation);
        if (definition is null)
        {
            issues.Add(new(path, $"Bilinmeyen ilişki: '{relation.Relation}'."));
            return;
        }

        if (!fields.CanRead(definition))
        {
            issues.Add(new(path, $"'{definition.Label}' ilişkisini görme izniniz yok."));
            return;
        }

        if (relation.Quantifier == RelationQuantifier.Count
            && (!CountOperators.Contains(relation.CountOperator) || relation.CountValue is < 0 or > 100_000))
        {
            issues.Add(new(path, "Sayı koşulu için karşılaştırma (=, ≠, >, ≥, <, ≤) ve 0-100000 arası bir sayı verin."));
        }

        if (relation.Where is { } where)
        {
            // İlişki koşulu hedef kaydın alanlarına bakar; eski değer ve değişiklik bilgisi yoktur, ikinci ilişki yoktur.
            Walk(where, fields.ForRelation(definition), ConditionOptions.Current, $"{path}.where", depth: 2, allowRelation: false, issues, ref leaves);
        }
    }

    private static void ValidateLeaf(ConditionLeaf leaf, AutomationFieldSet fields, ConditionOptions options, string path, List<AutomationIssue> issues)
    {
        var field = fields.Find(leaf.Field);
        if (field is null)
        {
            issues.Add(new(path, $"Bilinmeyen alan: '{leaf.Field}'."));
            return;
        }

        if (!fields.CanRead(field))
        {
            issues.Add(new(path, $"'{field.Label}' alanını görme izniniz yok; bu alanla koşul yazılamaz."));
            return;
        }

        if (!AutomationOperators.For(field.Type).Contains(leaf.Operator))
        {
            issues.Add(new(path, $"'{field.Label}' alanında bu işlem kullanılamaz."));
            return;
        }

        if (AutomationOperators.IsChange(leaf.Operator) && !options.AllowChange)
        {
            issues.Add(new(path, options.Step
                ? "Adım koşulu kaydın güncel değerine bakar; değişiklik koşulları yalnızca kuralın koşulunda kullanılabilir."
                : "Değişiklik koşulları yalnızca kayıt güncelleme tetikleyicilerinde (alan değişince, durum geçişi) kullanılabilir."));
            return;
        }

        if (leaf.Source == AutomationValueSource.Old && !options.AllowOld)
        {
            issues.Add(new(path, options.Step
                ? "Adım koşulu kaydın güncel değerine bakar; eski değer yalnızca kuralın koşulunda kullanılabilir."
                : "Eski değer yalnızca kayıt güncelleme tetikleyicilerinde (alan değişince, durum geçişi) kullanılabilir."));
        }

        var (min, max) = AutomationOperators.ValueCount(leaf.Operator);
        if (leaf.Values.Count < min || leaf.Values.Count > max)
        {
            issues.Add(new(path, min == max ? $"Bu işlem {min} değer ister." : $"Bu işlem {min}-{max} değer ister."));
            return;
        }

        if (field.Type == AutomationFieldType.Money)
        {
            if (leaf.Values.Count > 0 && !AutomationValues.IsCurrencyCode(leaf.Currency))
            {
                issues.Add(new(path, "Para alanında üç harfli para birimi (ör. TRY) gerekir; farklı para birimleri karşılaştırılmaz."));
                return;
            }
        }
        else if (leaf.Currency is not null)
        {
            issues.Add(new(path, "Para birimi yalnızca para alanında verilir."));
        }

        if (leaf.Values.Count == 0)
        {
            return;
        }

        if (leaf.Operator is AutomationOperator.WithinNextDays or AutomationOperator.WithinLastDays or AutomationOperator.OlderThanDays)
        {
            if (!AutomationValues.TryParseDays(leaf.Values[0], out _))
            {
                issues.Add(new(path, $"Gün sayısı 0-{AutomationValues.MaxDays} arasında bir tam sayı olmalı."));
            }

            return;
        }

        var parsed = new List<object?>();
        foreach (var text in leaf.Values)
        {
            if (!AutomationValues.TryParse(field.Type, field.Options, field.LookupKey, text, leaf.Currency, out var value, out var error))
            {
                issues.Add(new(path, error!));
                return;
            }

            parsed.Add(value);
        }

        if (leaf.Operator == AutomationOperator.Between && Compare(parsed[0], parsed[1]) > 0)
        {
            issues.Add(new(path, "Aralığın başlangıcı bitişinden büyük olamaz."));
        }
    }

    private static int Compare(object? left, object? right) => (left, right) switch
    {
        (decimal a, decimal b) => a.CompareTo(b),
        (AutomationMoney a, AutomationMoney b) => a.Amount.CompareTo(b.Amount),
        (DateOnly a, DateOnly b) => a.CompareTo(b),
        (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),
        _ => 0,
    };
}
