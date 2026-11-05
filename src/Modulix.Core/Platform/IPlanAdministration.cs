namespace Modulix.Core.Platform;

/// <summary>Plan özeti. <see cref="SubscriberCount"/>: bu plana geçerli (kapanmamış) abonesi olan firma sayısı.</summary>
public sealed record PlanInfo(
    Guid Id,
    string Key,
    string Name,
    bool IsActive,
    IReadOnlyList<string> Modules,
    IReadOnlyDictionary<string, int> Limits,
    int SubscriberCount);

/// <summary>Plan oluşturma/güncelleme girdisi. <see cref="Limits"/>'te olmayan limit sınırsızdır.</summary>
public sealed record PlanInput(string Key, string Name, IReadOnlyCollection<string> Modules, IReadOnlyDictionary<string, int>? Limits, bool IsActive = true);

/// <summary>Firmanın geçerli aboneliği.</summary>
public sealed record SubscriptionInfo(Guid PlanId, string PlanKey, string PlanName, DateTimeOffset StartsAt, DateTimeOffset? ValidUntil);

/// <summary>Bir limitin firmadaki durumu: çözülmüş değer ve güncel kullanım (kullanım çubuğu için).</summary>
public sealed record TenantLimitStatus(string Key, string Name, int? Limit, LimitSource Source, DateTimeOffset? OverrideUntil, int Usage);

/// <summary>Firmanın plan, limit ve istisna görünümü (platform yöneticisi için).</summary>
public sealed record TenantPlanOverview(SubscriptionInfo? Subscription, IReadOnlyList<TenantLimitStatus> Limits);

/// <summary>
/// Plan, abonelik ve limit yönetimi (ADR-0028 Dilim 4). Hiçbir kayıt silinmez: plan <c>IsActive</c> ile kapatılır, eski abonelik
/// kapatılır (<c>EndedAt</c>), limit istisnası geri alınır (<c>RevokedAt</c>); modül hakkı kapatılınca veri korunur. Erişim sınırı
/// yalnızca yeni kayıt/davet/yeniden etkinleştirme gibi eylemlere getirilir.
///
/// Hatalar: <see cref="UserAdministrationException"/> (bulunamadı, çakışma), geçersiz alan için <see cref="ArgumentException"/>
/// (ParamName alan adıdır). Her işlem platform denetim kaydına yazılır.
/// </summary>
public interface IPlanAdministration
{
    /// <summary>Etkin olmayanlar dahil bütün planlar, ada göre.</summary>
    Task<IReadOnlyList<PlanInfo>> ListPlansAsync(CancellationToken cancellationToken);

    /// <summary>Anahtar kullanılıyorsa çakışma; bilinmeyen/çekirdek modül ve limit için <see cref="ArgumentException"/>.</summary>
    Task<PlanInfo> CreatePlanAsync(PlanInput input, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Plan tanımını günceller (anahtar değişmez). Mevcut abonelerin modül hakları kendiliğinden değişmez; limitler hemen
    /// güncel plandan çözülür. Plan yoksa NotFound.
    /// </summary>
    Task<PlanInfo> UpdatePlanAsync(Guid planId, PlanInput input, Guid actingUserId, CancellationToken cancellationToken);

    Task<TenantPlanOverview> GetTenantPlanAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Firmaya planı atar ya da değiştirir. Planın modülleri kaynağı <c>Plan</c> olarak açılır (bitiş = <paramref name="validUntil"/>);
    /// önceki plandan gelen ve yeni planda olmayan modüller <b>kapatılır, veri silinmez</b>. Elle ya da istisna olarak verilmiş
    /// haklara dokunulmaz. Gerekçe zorunludur. Etkin olmayan plan, geçmiş bitiş, bilinmeyen firma için hata.
    /// </summary>
    Task AssignPlanAsync(Guid tenantId, string planKey, DateTimeOffset? validUntil, string reason, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Limit için geçici istisna (gerekçe ve bitiş zorunlu); aynı limitteki önceki istisna geri alınır.</summary>
    Task SetLimitOverrideAsync(Guid tenantId, string limitKey, int value, DateTimeOffset validUntil, string reason, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Geçerli istisnayı geri alır (kayıt kalır). Geçerli istisna yoksa çakışma.</summary>
    Task RevokeLimitOverrideAsync(Guid tenantId, string limitKey, string reason, Guid actingUserId, CancellationToken cancellationToken);
}
