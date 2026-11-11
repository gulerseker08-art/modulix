namespace Modulix.Shared.Crm;

// Satış analizi sözleşmeleri (ADR-0035, dilim 2). Alan adları API'deki modül sonuçlarıyla birebir aynıdır (uç nokta JSON üzerinden eşler).

/// <summary>Analiz zarfı: dönem, kapsam ve kısmi veri uyarılarıyla birlikte sonuç.</summary>
public sealed record AnalyticsDto<T>(
    string PeriodKind,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Today,
    string Currency,
    bool AmountsVisible,
    bool ScopeLimited,
    bool Truncated,
    T Data,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions);

// ---- Teklif kohortu

public sealed record QuoteOwnerCohortDto(Guid? OwnerId, int Families, int Accepted, decimal? AcceptanceRatePercent);

public sealed record QuoteCohortDto(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    DateTimeOffset AsOf,
    bool Mature,
    int Families,
    int Accepted,
    int Pending,
    int Rejected,
    int Cancelled,
    int Expired,
    decimal? AcceptanceRatePercent,
    int PresentedRevisions,
    decimal? AverageRevisionsPerFamily,
    decimal? RevisionAcceptanceRatePercent,
    decimal? AverageDaysToAccept,
    decimal? MedianDaysToAccept,
    MoneyTotalDto AcceptedNet,
    IReadOnlyList<QuoteOwnerCohortDto> ByOwner);

// ---- Lead kohortu

public sealed record LeadSourceCohortDto(string Source, int Leads, int Converted, decimal? RatePercent);

public sealed record LeadCohortDto(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    DateTimeOffset AsOf,
    int MaturityDays,
    bool Mature,
    int Leads,
    int Converted,
    int Disqualified,
    int Open,
    int Eligible,
    decimal? RateOfAllPercent,
    decimal? RateOfEligiblePercent,
    decimal? AverageDaysToConvert,
    decimal? MedianDaysToConvert,
    IReadOnlyList<LeadSourceCohortDto> BySource);

// ---- Aşama akışı

public sealed record StageFlowRowDto(string Stage, int Order, int Entered, int Advanced, decimal? AdvanceRatePercent, int LostFrom, int WonFrom, int StillHere);

public sealed record StageFlowDto(
    DateTimeOffset From, DateTimeOffset ToExclusive, DateTimeOffset AsOf, int Cohort, DateTimeOffset? TrackingStart, IReadOnlyList<StageFlowRowDto> Rows);

// ---- Ekip performansı

public sealed record OwnerPerformanceDto(
    Guid? OwnerId,
    MoneyTotalDto Realized,
    int WonCount,
    int LostCount,
    decimal? WinRatePercent,
    MoneyTotalDto OpenPipeline,
    MoneyTotalDto WeightedOpen,
    MoneyTotalDto ExpectedOpen,
    int OpenCount,
    int OverdueOpenCount,
    int CycleSample,
    decimal? AverageCycleDays,
    decimal? MedianCycleDays);

public sealed record TeamPerformanceRowDto(OwnerPerformanceDto Performance, decimal? Target, TargetProgressDto? Progress);

// ---- Veri kalitesi

public sealed record QualityItemDto(Guid Id, string Number, string Title, Guid? OwnerId, string? Detail);

public sealed record QualityIssueDto(string Code, int Count, IReadOnlyList<QualityItemDto> Samples);

public sealed record DataQualityDto(int OpenCount, IReadOnlyList<QualityIssueDto> Issues, int StaleDays);

/// <summary>Veri kalitesi sorun kodları.</summary>
public static class DataQualityCodes
{
    public const string MissingAmount = "MissingAmount";
    public const string MissingRate = "MissingRate";
    public const string MissingProbability = "MissingProbability";
    public const string MissingCloseDate = "MissingCloseDate";
    public const string Overdue = "Overdue";
    public const string Stale = "Stale";
    public const string NoHistory = "NoHistory";
}
