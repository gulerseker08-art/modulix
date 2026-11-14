using Modulix.Shared.Audit;
using Modulix.Shared.Customization;
using Modulix.Shared.Imports;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 16c); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Paylaşılan sözleşmelerdeki (Shared) etiketler (CustomFieldTypeLabels, FormSpanLabels, AuditText, SubProfileStatuses, SubProfileOptionKinds,
// ImportText) sunucuda ya da başka ekranlarda da kullanıldığı için Türkçe kalır; kaynak metin onlardan okunur. Tablolar özellik olarak
// tanımlıdır: metinler her okunuşta çözülür (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar eklenir
// (`SettingsLabelsTests` kapsamı denetler).

/// <summary>Yönetim ekranlarının (özel alanlar, form tasarımı, alt profiller, iletişim tercihleri, denetim kaydı, içe aktarma) etiketleri.</summary>
public static class SettingsLabels
{
    public const string FieldTypeGroup = "admin.custom-field-type";
    public const string SpanGroup = "admin.form-span";
    public const string AuditActionGroup = "admin.audit-action";
    public const string AuditSourceGroup = "admin.audit-source";
    public const string SubProfileStatusGroup = "admin.sub-profile-status";
    public const string SubProfileOptionGroup = "admin.sub-profile-option";
    public const string RuleEffectGroup = "admin.rule-effect";
    public const string ConsentStatusGroup = "admin.consent-status";
    public const string ConsentPurposeGroup = "admin.consent-purpose";
    public const string SuppressionReasonGroup = "admin.suppression-reason";
    public const string ImportStatusGroup = "admin.import-status";
    public const string ImportModeGroup = "admin.import-mode";
    public const string ImportPolicyGroup = "admin.import-policy";
    public const string ImportSeverityGroup = "admin.import-severity";
    public const string ImportCodeGroup = "admin.import-code";

    internal static IReadOnlyList<(string Key, string Label)> FieldTypeSource =>
        [.. Enum.GetValues<CustomFieldType>().Select(t => (t.ToString(), CustomFieldTypeLabels.Of(t)))];

    internal static IReadOnlyList<(string Key, string Label)> SpanSource =>
        [.. FormSpanLabels.All.Select(s => (s.ToString(System.Globalization.CultureInfo.InvariantCulture), FormSpanLabels.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> AuditActionSource =>
        [.. Enum.GetValues<AuditOperation>().Select(a => (a.ToString(), AuditText.Of(a)))];

    internal static IReadOnlyList<(string Key, string Label)> AuditSourceSource =>
        [.. Enum.GetValues<AuditSource>().Select(s => (s.ToString(), AuditText.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> SubProfileStatusSource =>
        [.. new[] { SubProfileStatuses.Draft, SubProfileStatuses.Active, SubProfileStatuses.Inactive }.Select(s => (s, SubProfileStatuses.Label(s)))];

    internal static IReadOnlyList<(string Key, string Label)> SubProfileOptionSource =>
        [.. new[] { SubProfileOptionKinds.Field, SubProfileOptionKinds.ListValue }.Select(k => (k, SubProfileOptionKinds.Label(k)))];

    /// <summary>Alt profil kuralının etkisi (ADR-0041 dilim 2); anahtar sunucudaki <c>FieldRuleDto.Effect</c> adıdır.</summary>
    internal static IReadOnlyList<(string Key, string Label)> RuleEffectSource =>
    [
        ("Required", "Zorunlu yap"),
        ("Hide", "Gizle"),
        ("Show", "Yalnızca koşul sağlanınca göster"),
        ("ReadOnly", "Salt okunur yap"),
        ("SuggestDefault", "Varsayılan değer öner"),
        ("Validate", "Kaydı reddet (hata iletisi)"),
        ("Warn", "Uyar (kaydı engellemez)"),
        ("RequireApproval", "Onay iste"),
        ("Block", "İşlemi engelle"),
    ];

    internal static IReadOnlyList<(string Key, string Label)> ConsentStatusSource => [("Granted", "İzinli"), ("Denied", "Reddedildi"), ("Unknown", "Bilinmiyor")];

    internal static IReadOnlyList<(string Key, string Label)> ConsentPurposeSource => [("Marketing", "Pazarlama"), ("Transactional", "İşlemsel"), ("Service", "Hizmet")];

    internal static IReadOnlyList<(string Key, string Label)> SuppressionReasonSource =>
        [("OptOut", "Çıkış (kişi istedi)"), ("Complaint", "Şikâyet"), ("HardBounce", "Kalıcı geri dönme"), ("InvalidAddress", "Geçersiz adres"), ("Manual", "Elle")];

    internal static IReadOnlyList<(string Key, string Label)> ImportStatusSource => [.. ImportText.Statuses.Select(s => (s, ImportText.Status(s)))];

    internal static IReadOnlyList<(string Key, string Label)> ImportModeSource => [.. new[] { "DryRun", "Apply" }.Select(m => (m, ImportText.Mode(m)))];

    internal static IReadOnlyList<(string Key, string Label)> ImportPolicySource =>
        [.. new[] { "SkipInvalidRows", "AllOrNothing" }.Select(p => (p, ImportText.Policy(p)))];

    internal static IReadOnlyList<(string Key, string Label)> ImportSeveritySource => [.. new[] { "Error", "Warning" }.Select(s => (s, ImportText.Severity(s)))];

    internal static IReadOnlyList<(string Key, string Label)> ImportCodeSource => [.. ImportText.Codes.Select(c => (c, ImportText.Code(c)))];

    public static IReadOnlyList<(string Key, string Label)> RuleEffects => AdminLabels.Translate(RuleEffectGroup, RuleEffectSource);

    public static IReadOnlyList<(string Key, string Label)> ConsentStatuses => AdminLabels.Translate(ConsentStatusGroup, ConsentStatusSource);

    public static IReadOnlyList<(string Key, string Label)> SuppressionReasons => AdminLabels.Translate(SuppressionReasonGroup, SuppressionReasonSource);

    public static IReadOnlyList<(string Key, string Label)> FieldTypes => AdminLabels.Translate(FieldTypeGroup, FieldTypeSource);

    public static IReadOnlyList<(string Key, string Label)> Spans => AdminLabels.Translate(SpanGroup, SpanSource);

    public static IReadOnlyList<(string Key, string Label)> AuditActions => AdminLabels.Translate(AuditActionGroup, AuditActionSource);

    public static IReadOnlyList<(string Key, string Label)> AuditSources => AdminLabels.Translate(AuditSourceGroup, AuditSourceSource);

    public static IReadOnlyList<(string Key, string Label)> SubProfileStatusLabels => AdminLabels.Translate(SubProfileStatusGroup, SubProfileStatusSource);

    public static IReadOnlyList<(string Key, string Label)> SubProfileOptionLabels => AdminLabels.Translate(SubProfileOptionGroup, SubProfileOptionSource);

    public static IReadOnlyList<(string Key, string Label)> ConsentPurposes => AdminLabels.Translate(ConsentPurposeGroup, ConsentPurposeSource);

    public static IReadOnlyList<(string Key, string Label)> ImportStatuses => AdminLabels.Translate(ImportStatusGroup, ImportStatusSource);

    public static IReadOnlyList<(string Key, string Label)> ImportModes => AdminLabels.Translate(ImportModeGroup, ImportModeSource);

    public static IReadOnlyList<(string Key, string Label)> ImportPolicies => AdminLabels.Translate(ImportPolicyGroup, ImportPolicySource);

    public static IReadOnlyList<(string Key, string Label)> ImportSeverities => AdminLabels.Translate(ImportSeverityGroup, ImportSeveritySource);

    public static IReadOnlyList<(string Key, string Label)> ImportCodes => AdminLabels.Translate(ImportCodeGroup, ImportCodeSource);

    public static string FieldType(CustomFieldType type) => AdminLabels.Of(FieldTypeGroup, type.ToString(), CustomFieldTypeLabels.Of(type));

    public static string Span(int span) =>
        AdminLabels.Lookup(SpanGroup, SpanSource, span.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Audit(AuditOperation action) => AdminLabels.Of(AuditActionGroup, action.ToString(), AuditText.Of(action));

    public static string Audit(AuditSource source) => AdminLabels.Of(AuditSourceGroup, source.ToString(), AuditText.Of(source));

    /// <summary>Kaynak ve varsa değişikliği yapan abonelik (ör. "Arka plan işi · core.assignment-email").</summary>
    public static string Audit(AuditSource source, string? subscription) => subscription is null ? Audit(source) : $"{Audit(source)} · {subscription}";

    public static string SubProfileStatus(string status) => AdminLabels.Lookup(SubProfileStatusGroup, SubProfileStatusSource, status);

    public static string SubProfileOption(string kind) => AdminLabels.Lookup(SubProfileOptionGroup, SubProfileOptionSource, kind);

    public static string RuleEffect(string effect) => AdminLabels.Lookup(RuleEffectGroup, RuleEffectSource, effect);

    public static string ConsentStatus(string status) => AdminLabels.Lookup(ConsentStatusGroup, ConsentStatusSource, status);

    public static string ConsentPurpose(string purpose) => AdminLabels.Lookup(ConsentPurposeGroup, ConsentPurposeSource, purpose);

    public static string SuppressionReason(string reason) => AdminLabels.Lookup(SuppressionReasonGroup, SuppressionReasonSource, reason);

    public static string ImportStatus(string status) => AdminLabels.Lookup(ImportStatusGroup, ImportStatusSource, status);

    public static string ImportMode(string mode) => AdminLabels.Lookup(ImportModeGroup, ImportModeSource, mode);

    public static string ImportPolicy(string policy) => AdminLabels.Lookup(ImportPolicyGroup, ImportPolicySource, policy);

    public static string ImportSeverity(string severity) => AdminLabels.Lookup(ImportSeverityGroup, ImportSeveritySource, severity);

    public static string ImportCode(string code) => AdminLabels.Lookup(ImportCodeGroup, ImportCodeSource, code);
}
