namespace Modulix.Shared.Contracts;

// ---- Dilim 9: kaydedilmiş görünümler, pano, dışa aktarma

/// <summary>Kullanıcının kaydettiği liste görünümü; <c>Filters</c> sorgu dizgisi biçiminde süzgeçtir (ör. <c>effectiveStatus=Active&amp;sort=-endDate</c>).</summary>
public sealed record ContractSavedViewDto(Guid Id, long Version, string Name, string? Filters);

public sealed record SaveContractSavedViewRequest(string Name, string? Filters = null, long? Version = null);

public sealed record DashboardCountDto(string Key, int Count);

public sealed record DashboardMoneyDto(string Currency, int Count, decimal NetTotal, decimal GrossTotal);

public sealed record DashboardDateDto(Guid ContractId, string Number, string Title, string Kind, DateOnly Date, int DaysLeft);

/// <summary><c>Money</c> yalnızca tutar iznine sahip kullanıcıya gelir (null = yetkisiz). <c>Truncated</c> doluysa dağılımlar ilk 2000 yürürlükteki sözleşmeye dayanır.</summary>
public sealed record ContractDashboardDto(
    DateOnly Today,
    IReadOnlyList<DashboardCountDto> Counts,
    IReadOnlyList<DashboardCountDto> ByType,
    IReadOnlyList<DashboardMoneyDto>? Money,
    IReadOnlyList<DashboardDateDto> Upcoming,
    bool Truncated);
