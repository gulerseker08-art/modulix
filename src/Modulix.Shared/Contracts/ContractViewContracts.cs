namespace Modulix.Shared.Contracts;

// ---- Dilim 9: pano ve dışa aktarma (kaydedilmiş görünümler ortak core.GridViews'a taşındı, ADR-0036)

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
