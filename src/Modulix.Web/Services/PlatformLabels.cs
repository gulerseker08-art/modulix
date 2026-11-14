using Modulix.Shared.Platform;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, Platform P1); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Paylaşılan sözleşmelerdeki (Shared) etiketler (TenantStateText, LicenseSourceText, EventStateText, PlatformAuditText, PlatformRoleNames)
// sunucuyla ortak sözleşmede durduğu için Türkçe kalır; kaynak metin onlardan okunur. Tablolar özellik olarak tanımlıdır: metinler her okunuşta
// çözülür (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar eklenir (`PlatformLabelsTests` kapsamı denetler).

/// <summary>Platform yönetimi ekranlarının (firmalar, modül hakları, ekip, olaylar, denetim, ödemeler) etiketleri.</summary>
public static class PlatformLabels
{
    public const string TenantStateGroup = "platform.tenant-state";
    public const string LicenseSourceGroup = "platform.license-source";
    public const string EventStateGroup = "platform.event-state";
    public const string AuditActionGroup = "platform.audit-action";
    public const string RoleGroup = "platform.role";
    public const string BillingStatusGroup = "platform.billing-status";

    /// <summary>Ödeme kaydının ekrandaki durumu; vadesi geçen bekleyen kayıt <c>Overdue</c> olarak gösterilir.</summary>
    public const string Overdue = "Overdue";

    internal static IReadOnlyList<(string Key, string Label)> TenantStateSource =>
        [.. Enum.GetValues<TenantState>().Select(s => (s.ToString(), TenantStateText.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> LicenseSourceSource =>
        [.. Enum.GetValues<LicenseSource>().Select(s => (s.ToString(), LicenseSourceText.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> EventStateSource =>
        [.. Enum.GetValues<EventDeliveryStateDto>().Select(s => (s.ToString(), EventStateText.Of(s)))];

    internal static IReadOnlyList<(string Key, string Label)> AuditActionSource => PlatformAuditText.All;

    internal static IReadOnlyList<(string Key, string Label)> RoleSource =>
        [.. new[] { PlatformRoleNames.Support, PlatformRoleNames.Operations, PlatformRoleNames.Finance, PlatformRoleNames.Owner }
            .Select(r => (r, PlatformRoleNames.TextOf(r)))];

    internal static IReadOnlyList<(string Key, string Label)> BillingStatusSource =>
    [
        (nameof(BillingStatusDto.Pending), "Bekliyor"),
        (Overdue, "Vadesi geçti"),
        (nameof(BillingStatusDto.Paid), "Ödendi"),
        (nameof(BillingStatusDto.Cancelled), "İptal"),
    ];

    public static IReadOnlyList<(string Key, string Label)> TenantStates => AdminLabels.Translate(TenantStateGroup, TenantStateSource);

    public static IReadOnlyList<(string Key, string Label)> LicenseSources => AdminLabels.Translate(LicenseSourceGroup, LicenseSourceSource);

    public static IReadOnlyList<(string Key, string Label)> EventStates => AdminLabels.Translate(EventStateGroup, EventStateSource);

    /// <summary>Platform denetim kaydı işlemleri, süzgeçteki sırayla.</summary>
    public static IReadOnlyList<(string Key, string Label)> AuditActions => AdminLabels.Translate(AuditActionGroup, AuditActionSource);

    public static IReadOnlyList<(string Key, string Label)> Roles => AdminLabels.Translate(RoleGroup, RoleSource);

    public static IReadOnlyList<(string Key, string Label)> BillingStatuses => AdminLabels.Translate(BillingStatusGroup, BillingStatusSource);

    public static string TenantStateOf(TenantState state) => AdminLabels.Of(TenantStateGroup, state.ToString(), TenantStateText.Of(state));

    public static string LicenseSourceOf(LicenseSource source) => AdminLabels.Of(LicenseSourceGroup, source.ToString(), LicenseSourceText.Of(source));

    public static string EventStateOf(EventDeliveryStateDto state) => AdminLabels.Of(EventStateGroup, state.ToString(), EventStateText.Of(state));

    /// <summary>Bilinmeyen işlem anahtarı olduğu gibi gösterilir.</summary>
    public static string AuditActionOf(string action) => AdminLabels.Lookup(AuditActionGroup, AuditActionSource, action);

    /// <summary>Bilinmeyen rol adı olduğu gibi gösterilir.</summary>
    public static string RoleOf(string role) => AdminLabels.Lookup(RoleGroup, RoleSource, role);

    public static string BillingStatusOf(BillingRecordDto item) =>
        AdminLabels.Lookup(BillingStatusGroup, BillingStatusSource, item.Status == BillingStatusDto.Pending && item.IsOverdue ? Overdue : item.Status.ToString());
}
