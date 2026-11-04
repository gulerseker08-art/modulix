namespace Modulix.Shared.Platform;

// Plan, abonelik ve limit sözleşmeleri (ADR-0028 Dilim 4): /api/platform/plans ve /api/platform/tenants/{id}/plan.

/// <summary>Plan. <see cref="Limits"/>'te olmayan limit sınırsızdır; <see cref="SubscriberCount"/> geçerli abone firma sayısıdır.</summary>
public sealed record PlanDto(
    Guid Id, string Key, string Name, bool IsActive, IReadOnlyList<string> Modules, IReadOnlyDictionary<string, int> Limits, int SubscriberCount);

/// <summary>Plan oluşturma/güncelleme. Anahtar yalnızca oluştururken kullanılır, sonradan değişmez.</summary>
public sealed record SavePlanRequest(
    string Key, string Name, IReadOnlyList<string>? Modules = null, IReadOnlyDictionary<string, int>? Limits = null, bool IsActive = true);

/// <summary>Satışa konu limit tanımı (ekranda alan adı için).</summary>
public sealed record LimitDefinitionDto(string Key, string Name);

public sealed record PlansResponse(IReadOnlyList<PlanDto> Plans, IReadOnlyList<LimitDefinitionDto> Limits);

public enum LimitSourceDto
{
    None = 0,
    Plan = 1,
    Override = 2,
}

public sealed record SubscriptionDto(Guid PlanId, string PlanKey, string PlanName, DateTimeOffset StartsAt, DateTimeOffset? ValidUntil);

/// <summary>Limitin firmadaki durumu. <see cref="Limit"/> boşsa sınırsız; <see cref="Usage"/> güncel kullanımdır.</summary>
public sealed record TenantLimitDto(string Key, string Name, int? Limit, LimitSourceDto Source, DateTimeOffset? OverrideUntil, int Usage);

public sealed record TenantPlanResponse(SubscriptionDto? Subscription, IReadOnlyList<TenantLimitDto> Limits);

/// <summary>Plan atama: gerekçe zorunludur; <see cref="ValidUntil"/> boşsa süresiz.</summary>
public sealed record AssignPlanRequest(string PlanKey, string Reason, DateTimeOffset? ValidUntil = null);

/// <summary>Limit istisnası: gerekçe ve gelecekte bir bitiş zorunludur.</summary>
public sealed record SetLimitOverrideRequest(int Value, DateTimeOffset ValidUntil, string Reason);

public sealed record RevokeLimitOverrideRequest(string Reason);
