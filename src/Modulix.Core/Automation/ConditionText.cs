using System.Globalization;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>
/// Koşulun ve tetikleyicinin okunabilir Türkçe metni ("Tutar ≥ 5000 TRY ve Aşama şunlardan biri: Teklif, Müzakere"). Kural özetinde ve
/// değerlendirme açıklamasında kullanılır; kayıt değeri içermez. Metin şablondan üretilir, yapay zekâ ürünü değildir.
/// </summary>
public static class ConditionText
{
    public static string Describe(ConditionNode? node, AutomationFieldSet fields) =>
        node is null ? "Her kayıt için" : Render(node, fields, topLevel: true);

    public static string Leaf(ConditionLeaf leaf, AutomationFieldSet fields)
    {
        var field = fields.Find(leaf.Field);
        var label = field?.Label ?? leaf.Field;
        if (AutomationOperators.IsChange(leaf.Operator))
        {
            var changed = leaf.Operator switch
            {
                AutomationOperator.ChangedTo => $"şuna değişti: {Values(leaf, field)}",
                AutomationOperator.ChangedFrom => $"şundan değişti: {Values(leaf, field)}",
                _ => "değişti",
            };
            return $"{label} {changed}";
        }

        var prefix = leaf.Source == AutomationValueSource.Old ? "Eski " : string.Empty;
        var name = prefix.Length > 0 ? prefix + char.ToLower(label[0], CultureInfo.GetCultureInfo("tr-TR")) + label[1..] : label;
        var values = Values(leaf, field);
        return leaf.Operator switch
        {
            AutomationOperator.Eq => $"{name} = {values}",
            AutomationOperator.Ne => $"{name} ≠ {values}",
            AutomationOperator.Gt => $"{name} > {values}",
            AutomationOperator.Gte => $"{name} ≥ {values}",
            AutomationOperator.Lt => $"{name} < {values}",
            AutomationOperator.Lte => $"{name} ≤ {values}",
            AutomationOperator.Between => $"{name} {Join(leaf, field, 0)} ile {Join(leaf, field, 1)} arasında",
            AutomationOperator.Contains => $"{name} şunu içerir: {values}",
            AutomationOperator.StartsWith => $"{name} şununla başlar: {values}",
            AutomationOperator.In => $"{name} şunlardan biri: {values}",
            AutomationOperator.NotIn => $"{name} şunlardan hiçbiri değil: {values}",
            AutomationOperator.IsEmpty => $"{name} boş",
            AutomationOperator.IsNotEmpty => $"{name} dolu",
            AutomationOperator.Before => $"{name} {values} öncesinde",
            AutomationOperator.After => $"{name} {values} sonrasında",
            AutomationOperator.WithinNextDays => $"{name} önümüzdeki {values} gün içinde",
            AutomationOperator.WithinLastDays => $"{name} son {values} gün içinde",
            AutomationOperator.OlderThanDays => $"{name} en az {values} gün önce",
            _ => name,
        };
    }

    public static string Trigger(AutomationTrigger trigger, AutomationFieldSet fields) => trigger switch
    {
        RecordCreatedTrigger => "Kayıt oluşturulunca",
        FieldChangedTrigger changed => $"Şu alanlardan biri değişince: {string.Join(", ", changed.Fields.Select(f => fields.Find(f)?.Label ?? f))}",
        StatusTransitionTrigger transition =>
            $"{fields.Find(transition.Field)?.Label ?? transition.Field} {(transition.From is null ? "herhangi bir değerden" : OptionLabel(fields.Find(transition.Field), transition.From) + " değerinden")} {OptionLabel(fields.Find(transition.Field), transition.To)} değerine geçince",
        ScheduledTrigger scheduled => $"Her gün {scheduled.TimeOfDay} (firma saatiyle) taranır",
        DateRelativeTrigger relative => relative.OffsetDays switch
        {
            < 0 => $"{fields.Find(relative.Field)?.Label ?? relative.Field} tarihine {-relative.OffsetDays} gün kala",
            0 => $"{fields.Find(relative.Field)?.Label ?? relative.Field} tarihinde",
            _ => $"{fields.Find(relative.Field)?.Label ?? relative.Field} tarihinden {relative.OffsetDays} gün sonra",
        },
        InactivityTrigger inactivity => $"{fields.FindRelation(inactivity.Relation)?.Label ?? inactivity.Relation} için {inactivity.Days} gündür hareket yoksa",
        DomainEventTrigger domainEvent => $"İş olayı gerçekleşince: {domainEvent.EventKey}",
        ManualTrigger => "Yetkili kullanıcı elle çalıştırınca",
        _ => "Bilinmeyen tetikleyici",
    };

    private static string Render(ConditionNode node, AutomationFieldSet fields, bool topLevel)
    {
        switch (node)
        {
            case ConditionLeaf leaf:
                return Leaf(leaf, fields);

            case ConditionRelation relation:
                var label = fields.FindRelation(relation.Relation)?.Label ?? relation.Relation;
                var where = relation.Where is null
                    ? string.Empty
                    : $" ({Render(relation.Where, fields.FindRelation(relation.Relation) is { } r ? fields.ForRelation(r) : fields, topLevel: false)})";
                return relation.Quantifier switch
                {
                    RelationQuantifier.Exists => $"{label} var{where}",
                    RelationQuantifier.NotExists => $"{label} yok{where}",
                    _ => $"{label} sayısı {Symbol(relation.CountOperator)} {relation.CountValue}{where}",
                };

            case ConditionGroup group:
                var joiner = group.Logic == ConditionLogic.And ? " ve " : " veya ";
                var text = string.Join(joiner, group.Items.Select(i => Render(i, fields, topLevel: false)));
                return topLevel || group.Items.Count < 2 ? text : $"({text})";

            default:
                return "?";
        }
    }

    private static string Symbol(AutomationOperator op) => op switch
    {
        AutomationOperator.Eq => "=",
        AutomationOperator.Ne => "≠",
        AutomationOperator.Gt => ">",
        AutomationOperator.Gte => "≥",
        AutomationOperator.Lt => "<",
        AutomationOperator.Lte => "≤",
        _ => "?",
    };

    private static string Values(ConditionLeaf leaf, AutomationFieldDefinition? field) =>
        string.Join(", ", leaf.Values.Select((_, i) => Join(leaf, field, i)));

    private static string Join(ConditionLeaf leaf, AutomationFieldDefinition? field, int index)
    {
        var text = index < leaf.Values.Count ? leaf.Values[index] : string.Empty;
        if (field?.Type == AutomationFieldType.Choice)
        {
            return OptionLabel(field, text);
        }

        return field?.Type == AutomationFieldType.Money && leaf.Currency is not null ? $"{text} {leaf.Currency}" : text;
    }

    private static string OptionLabel(AutomationFieldDefinition? field, string key) =>
        field?.Options.FirstOrDefault(o => o.Key == key)?.Label ?? key;
}
