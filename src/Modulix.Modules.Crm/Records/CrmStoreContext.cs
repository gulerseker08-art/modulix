using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Opportunities;

namespace Modulix.Modules.Crm.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler: veri, tenant üyeleri, seçim listeleri ve isteği yapan kullanıcı.</summary>
internal sealed class CrmStoreContext(
    IModuleData data, ITenantMembers members, IChoiceLists choiceLists, ITenantContextAccessor tenant, IRecordAccess access,
    ICurrentAccess permissions, IEnumerable<IActivityLinkTarget> linkTargets, LeadWorkflowGate? leadGate = null,
    ActivityWorkflowGate? activityGate = null, IRecordCommandContext? command = null, OpportunityWorkflowGate? opportunityGate = null,
    IOpportunityPolicyProvider? policy = null)
{
    /// <summary>Fırsat iş akışı kapısı (<see cref="OpportunityWorkflowGate"/>).</summary>
    public OpportunityWorkflowGate OpportunityGate { get; } = opportunityGate ?? new OpportunityWorkflowGate();

    /// <summary>Tenant ayarlarından gelen fırsat politikası; sağlayıcı yoksa varsayılan (koşulsuz) politika.</summary>
    public async Task<OpportunityPolicy> GetPolicyAsync(CancellationToken cancellationToken) =>
        policy is null ? OpportunityPolicy.Default : await policy.GetAsync(cancellationToken);

    /// <summary>Çalışan yazma komutunun nedeni (yalnızca güvenilir iç çağıranlar verir, ör. kayıt devri).</summary>
    public string? CommandReason => command?.Reason;

    public IModuleData Data => data;

    /// <summary>Kullanıcının izinleri (kayıt türünden bağımsız izin sorguları için, ör. Lead ataması).</summary>
    public ICurrentAccess Permissions => permissions;

    /// <summary>Lead iş akışı kapısı (<see cref="LeadWorkflowGate"/>).</summary>
    public LeadWorkflowGate LeadGate { get; } = leadGate ?? new LeadWorkflowGate();

    /// <summary>Aktivite iş akışı kapısı (<see cref="ActivityWorkflowGate"/>).</summary>
    public ActivityWorkflowGate ActivityGate { get; } = activityGate ?? new ActivityWorkflowGate();

    /// <summary>Aktivite ilişkisine açılmış kayıt türleri (ADR-0015); her modül kendi hedefini kaydeder.</summary>
    public IReadOnlyList<IActivityLinkTarget> LinkTargets { get; } = [.. linkTargets];

    /// <summary>İsteği yapan kullanıcının izin ve veri kapsamı (ADR-0010).</summary>
    public IRecordAccess Access => access;

    public ITenantMembers Members => members;

    public IChoiceLists ChoiceLists => choiceLists;

    /// <summary>İsteği yapan kullanıcı; aktif tenant yoksa null.</summary>
    public Guid? CurrentUserId => tenant.Current?.UserId;
}

/// <summary>
/// Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).
/// </summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value, CrmStoreContext context);

/// <summary>Veri okuması gereken filtre (ör. firmanın listesindeki kategoriye göre süzme).</summary>
internal delegate Task<IQueryable<TEntity>?> AsyncRecordFilter<TEntity>(
    IQueryable<TEntity> source, string value, CrmStoreContext context, CancellationToken cancellationToken);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    /// <summary>"me" isteği yapan kullanıcıdır; aksi halde kullanıcı kimliği beklenir.</summary>
    public static Guid? User(string value, CrmStoreContext context) =>
        string.Equals(value, "me", StringComparison.OrdinalIgnoreCase) ? context.CurrentUserId : Id(value);

    public static bool? Bool(string value) => bool.TryParse(value, out var flag) ? flag : null;

    /// <summary>Firmanın listesindeki değer (Y9b, pasifler dahil); listede yoksa null. Kayıttaki anahtarı döner.</summary>
    public static async Task<string?> ChoiceAsync(string value, string listKey, CrmStoreContext context, CancellationToken cancellationToken) =>
        ChoiceValueKeys.IsValid(value) ? (await context.ChoiceLists.GetAsync(listKey, cancellationToken)).Find(value)?.Key : null;

    public static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;

    public static DateTimeOffset? Time(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime()
            : null;
}
