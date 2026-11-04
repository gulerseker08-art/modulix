using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.FieldRules;

/// <summary>
/// Alan kuralının etkisi (ADR-0029 K7). Hepsi bildirimseldir: serbest SQL ya da kod yoktur. Koşul, etkinin ne zaman geçerli olduğunu
/// söyler; <see cref="Validate"/>, <see cref="Warn"/>, <see cref="RequireApproval"/> ve <see cref="Block"/> için koşul "bu durumda bildir"
/// anlamındadır (ör. "tür = ihracat ve gümrük kodu boş").
/// </summary>
public enum FieldRuleEffect
{
    /// <summary>Alan yalnızca koşul sağlanınca görünür. Alanın hiç Show kuralı yoksa varsayılan görünürdür.</summary>
    Show,

    /// <summary>Koşul sağlanınca alan gizlenir. Değer silinmez.</summary>
    Hide,

    ReadOnly,

    /// <summary>Koşul sağlanınca zorunlu. Gizli alan zorunlu sayılmaz.</summary>
    Required,

    /// <summary>Boş alana öneri değeri. Kullanıcı değiştirebilir; zorlayıcı değildir.</summary>
    SuggestDefault,

    /// <summary>Koşul sağlanınca kayıt reddedilir (hata ileti).</summary>
    Validate,

    /// <summary>Koşul sağlanınca uyarı gösterilir, kayıt engellenmez.</summary>
    Warn,

    /// <summary>Koşul sağlanınca kayıt onay gerektirir.</summary>
    RequireApproval,

    /// <summary>Koşul sağlanınca işlem engellenir.</summary>
    Block,
}

/// <summary>
/// Tek alan kuralı. <see cref="Key"/> paket/katman birleşiminde kuralın kimliğidir (üst katmanı geçersiz kılmak ya da devre dışı
/// bırakmak buna göre olur). <see cref="Field"/> kural alanı anahtarıdır (<see cref="AutomationFieldDefinition.Key"/>; ADR-0027 ile
/// aynı alan kümesi). <see cref="Locked"/> kural, alt katmanca (tenant uyarlaması vb.) geçersiz kılınamaz ya da kapatılamaz.
/// </summary>
public sealed record FieldRule(string Key, string Field, FieldRuleEffect Effect, ConditionNode? Condition = null)
{
    public const int KeyMaxLength = 100;
    public const int MessageMaxLength = 200;

    /// <summary>İleti: Validate, Warn, RequireApproval ve Block için zorunludur.</summary>
    public string? Message { get; init; }

    /// <summary>Öneri değeri: yalnızca SuggestDefault için; alan tipine göre metin olarak yazılır (ADR-0027 koşul değerleriyle aynı).</summary>
    public string? Value { get; init; }

    public bool Locked { get; init; }

    public static bool NeedsMessage(FieldRuleEffect effect) =>
        effect is FieldRuleEffect.Validate or FieldRuleEffect.Warn or FieldRuleEffect.RequireApproval or FieldRuleEffect.Block;
}

/// <summary>Kural tanımını yayın öncesi doğrular. Koşul ADR-0027'nin <see cref="ConditionValidator"/>'ıyla denetlenir (derinlik, operatör, alan tipi).</summary>
public static class FieldRuleValidator
{
    public static IReadOnlyList<AutomationIssue> Validate(FieldRule rule, AutomationFieldSet fields)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(fields);
        var issues = new List<AutomationIssue>();

        if (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > FieldRule.KeyMaxLength || !rule.Key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            issues.Add(new AutomationIssue("key", "Kural anahtarı küçük harf, rakam, '-' ve '.' içerebilir (en çok 100 karakter)."));
        }

        if (!Enum.IsDefined(rule.Effect))
        {
            issues.Add(new AutomationIssue("effect", "Geçersiz kural etkisi."));
        }

        if (fields.Find(rule.Field) is null)
        {
            issues.Add(new AutomationIssue("field", $"Alan bu kayıt türünde yok: '{rule.Field}'."));
        }

        if (FieldRule.NeedsMessage(rule.Effect))
        {
            if (string.IsNullOrWhiteSpace(rule.Message))
            {
                issues.Add(new AutomationIssue("message", "Bu etki için ileti zorunludur."));
            }
            else if (rule.Message.Length > FieldRule.MessageMaxLength)
            {
                issues.Add(new AutomationIssue("message", $"İleti en çok {FieldRule.MessageMaxLength} karakter olabilir."));
            }
        }

        if (rule.Effect == FieldRuleEffect.SuggestDefault && string.IsNullOrWhiteSpace(rule.Value))
        {
            issues.Add(new AutomationIssue("value", "Öneri değeri boş olamaz."));
        }

        if (rule.Effect != FieldRuleEffect.SuggestDefault && rule.Value is not null)
        {
            issues.Add(new AutomationIssue("value", "Değer yalnızca öneri kuralında kullanılır."));
        }

        if (rule.Condition is not null)
        {
            issues.AddRange(ConditionValidator.Validate(rule.Condition, fields, ConditionOptions.Current)
                .Select(i => new AutomationIssue("condition" + (i.Path.Length == 0 ? string.Empty : "." + i.Path), i.Message)));
        }

        return issues;
    }
}
