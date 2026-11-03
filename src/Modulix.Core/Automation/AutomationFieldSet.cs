using System.Globalization;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>Kural tanımında bulunan bir sorun: nerede (yol) ve ne. İleti kullanıcıya gösterilir.</summary>
public sealed record AutomationIssue(string Path, string Message);

/// <summary>
/// Bir kayıt türünün kuralda kullanılabilen alanları ve ilişkileri (modülün beyanı + firmanın özel alanları) ve değerlendiren
/// kimliğin izinleri. Doğrulayıcı ve değerlendirici yalnızca bunu bilir; modülü veya katalogu bilmez.
/// </summary>
public sealed class AutomationFieldSet
{
    private readonly Dictionary<string, AutomationFieldDefinition> fields;
    private readonly Dictionary<string, AutomationRelationDefinition> relations;
    private readonly IReadOnlySet<string> granted;

    public AutomationFieldSet(
        string entityKey,
        IEnumerable<AutomationFieldDefinition> fields,
        IEnumerable<AutomationRelationDefinition> relations,
        IReadOnlySet<string> grantedPermissions)
    {
        EntityKey = entityKey;
        this.fields = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        this.relations = relations.ToDictionary(r => r.Key, StringComparer.Ordinal);
        granted = grantedPermissions;
    }

    public string EntityKey { get; }

    public IEnumerable<AutomationFieldDefinition> Fields => fields.Values;

    public IEnumerable<AutomationRelationDefinition> Relations => relations.Values;

    public AutomationFieldDefinition? Find(string key) => fields.GetValueOrDefault(key);

    public AutomationRelationDefinition? FindRelation(string key) => relations.GetValueOrDefault(key);

    /// <summary>Değerlendiren kimlik alanı görebilir mi (alan yetkisi)?</summary>
    public bool CanRead(AutomationFieldDefinition field) => field.ReadPermission is null || granted.Contains(field.ReadPermission);

    public bool CanRead(AutomationRelationDefinition relation) => relation.ReadPermission is null || granted.Contains(relation.ReadPermission);

    /// <summary>İlişkinin hedef kayıt türünün alan kümesi (<c>Where</c> koşulu için); ikinci seviye ilişki içermez.</summary>
    public AutomationFieldSet ForRelation(AutomationRelationDefinition relation) =>
        new(relation.TargetEntityKey, relation.TargetFields, [], granted);
}

/// <summary>Koşul ve aksiyon parametresi değerlerinin metinden tipine çözülmesi (kültürden bağımsız).</summary>
public static class AutomationValues
{
    public const int MaxTextLength = 200;

    public const int MaxDays = 3650;

    /// <summary>Gün sayısı değeri (0..3650).</summary>
    public static bool TryParseDays(string text, out int days) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out days) && days <= MaxDays;

    /// <summary>
    /// Metni alan tipine göre çözer. <paramref name="currency"/> para alanında zorunludur (çağıran denetler). Hata iletisi
    /// kullanıcıya gösterilir.
    /// </summary>
    public static bool TryParse(
        AutomationFieldType type, IReadOnlyList<AutomationOption> options, string? lookupKey, string text, string? currency,
        out object? value, out string? error)
    {
        value = null;
        error = null;
        switch (type)
        {
            case AutomationFieldType.Text:
                if (text.Length is 0 or > MaxTextLength)
                {
                    error = $"Metin 1-{MaxTextLength} karakter olmalı.";
                    return false;
                }

                value = text;
                return true;

            case AutomationFieldType.Number:
                if (!TryDecimal(text, out var number))
                {
                    error = $"'{text}' geçerli bir sayı değil (ondalık ayracı nokta).";
                    return false;
                }

                value = number;
                return true;

            case AutomationFieldType.Money:
                if (!TryDecimal(text, out var amount))
                {
                    error = $"'{text}' geçerli bir tutar değil (ondalık ayracı nokta).";
                    return false;
                }

                value = new AutomationMoney(amount, currency ?? string.Empty);
                return true;

            case AutomationFieldType.Boolean:
                if (!bool.TryParse(text, out var flag))
                {
                    error = "Değer 'true' ya da 'false' olmalı.";
                    return false;
                }

                value = flag;
                return true;

            case AutomationFieldType.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    error = $"'{text}' geçerli bir tarih değil (yyyy-AA-gg).";
                    return false;
                }

                value = date;
                return true;

            case AutomationFieldType.DateTime:
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                {
                    error = $"'{text}' geçerli bir zaman değil (ISO 8601).";
                    return false;
                }

                value = moment.ToUniversalTime();
                return true;

            case AutomationFieldType.Choice:
                if (text.Length == 0 || (options.Count > 0 && options.All(o => o.Key != text)))
                {
                    error = $"'{text}' izin verilen seçeneklerden biri değil.";
                    return false;
                }

                value = text;
                return true;

            case AutomationFieldType.Lookup:
                if (!Guid.TryParse(text, out var id) || id == Guid.Empty)
                {
                    error = "Değer kaydın kalıcı kimliği olmalı (görünen ad değil).";
                    return false;
                }

                value = id;
                return true;

            default:
                error = "Bilinmeyen alan tipi.";
                return false;
        }
    }

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    public static bool IsCurrencyCode(string? code) => code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');
}
