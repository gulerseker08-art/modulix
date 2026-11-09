namespace Modulix.Shared.Crm;

// Satış raporlama, hedef ve sürümlü tahmin sözleşmeleri (ADR-0035). Alan adları API'deki modül sonuçlarıyla birebir aynıdır (uç nokta JSON üzerinden eşler).

/// <summary>Dönem türü adları (sorgu parametresi <c>kind</c> ve yanıttaki <c>periodKind</c>).</summary>
public static class SalesPeriodKinds
{
    public const string Month = "Month";
    public const string Quarter = "Quarter";
    public const string Year = "Year";

    public static IReadOnlyList<string> All { get; } = [Month, Quarter, Year];
}

public static class SalesTargetMetrics
{
    public const string WonAmount = "WonAmount";
    public const string WonCount = "WonCount";
}

public static class SalesTargetScopes
{
    public const string Tenant = "Tenant";
    public const string User = "User";
}

/// <summary>Tahmin kategorileri (birbirini dışlar), durumlar ve düzeltme türleri.</summary>
public static class ForecastCategories
{
    public const string Omitted = "Omitted";
    public const string Pipeline = "Pipeline";
    public const string BestCase = "BestCase";
    public const string Commit = "Commit";

    public static IReadOnlyList<string> All { get; } = [Commit, BestCase, Pipeline, Omitted];
}

public static class ForecastStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Locked = "Locked";
}

public static class ForecastAdjustmentKinds
{
    public const string Category = "Category";
    public const string Amount = "Amount";
    public const string Probability = "Probability";
}

/// <summary>Panel kartları; detay listesi <c>/api/crm/sales/overview/cards/{card}</c> ile açılır.</summary>
public static class SalesCards
{
    public const string OpenPipeline = "OpenPipeline";
    public const string ExpectedOpen = "ExpectedOpen";
    public const string WeightedOpen = "WeightedOpen";
    public const string Realized = "Realized";
    public const string Lost = "Lost";
}

// ---- Hedef

public sealed record SalesTargetDto(
    Guid Id, long Version, string Metric, string ScopeType, Guid? ScopeUserId, string? Currency, DateOnly PeriodStart, DateOnly PeriodEnd,
    decimal Value, int RevisionNo, string? Note);

public sealed record SaveSalesTargetRequest(
    string Metric, string ScopeType, Guid? ScopeUserId, string? Currency, DateOnly PeriodStart, DateOnly PeriodEnd, decimal Value,
    string? Note = null, string? ChangeReason = null, long? Version = null);

// ---- Satış genel görünümü

public sealed record MoneyTotalDto(decimal Amount, int Count, int MissingAmountCount, int MissingRateCount, bool IsPartial = false);

public sealed record TargetProgressDto(decimal Target, decimal Realized, decimal Remaining, decimal? AttainmentPercent, decimal? Coverage, bool TargetMet);

public sealed record TargetBlockDto(string Metric, string Source, decimal? Value, TargetProgressDto? Progress, string? Problem, decimal? ChildExcess, string? Currency);

public sealed record StageSliceDto(string Stage, int Count, decimal Amount, decimal Weighted, int MissingAmountCount, int MissingRateCount, decimal? AverageDaysInStage = null);

/// <summary>Önceki dönemin karşılaştırma penceresi: dönem sürüyorsa aynı gün sayısı, bittiyse tamamı.</summary>
public sealed record PeriodComparisonDto(string Label, DateOnly Start, DateOnly LastDay, MoneyTotalDto Realized, int WonCount, int LostCount, decimal? WinRatePercent);

public sealed record TrendPointDto(string Label, DateOnly Start, DateOnly End, decimal Realized, int WonCount, bool Partial);

public sealed record OverviewRiskDto(Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, DateOnly? ExpectedCloseDate, decimal? Amount, string Currency);

public sealed record SalesOverviewDto(
    string PeriodKind,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Today,
    string Currency,
    bool AmountsVisible,
    bool ScopeLimited,
    bool Truncated,
    MoneyTotalDto OpenPipeline,
    MoneyTotalDto ExpectedOpen,
    MoneyTotalDto WeightedOpen,
    int WeightedMissingProbabilityCount,
    MoneyTotalDto Realized,
    decimal Outlook,
    int WonCount,
    int LostCount,
    decimal? WinRatePercent,
    int OpenWithoutCloseDateCount,
    int OverdueOpenCount,
    TargetBlockDto? AmountTarget,
    TargetBlockDto? CountTarget,
    IReadOnlyList<StageSliceDto> ByStage,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<OverviewRiskDto> Overdue,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions,
    PeriodComparisonDto? PreviousToDate = null);

public sealed record SalesDetailRowDto(
    Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, string Status, string Stage, decimal? SourceAmount, string SourceCurrency,
    decimal? Amount, string? Issue, int? Probability, DateOnly? ExpectedCloseDate, DateOnly? ActualCloseDate, decimal? RateUsed);

public sealed record SalesCardDetailDto(
    string Card, string PeriodLabel, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, bool AmountsVisible, bool Truncated,
    MoneyTotalDto Total, int RowCount, IReadOnlyList<SalesDetailRowDto> Rows);

// ---- Tahmin

public sealed record CreateForecastRequestDto(string? Kind, DateOnly? Anchor, string? ScopeType, Guid? ScopeUserId, string? Currency);

public sealed record AdjustForecastRequestDto(Guid ItemId, string? Kind, string? Value, string? Reason, long? Version = null);

public sealed record ForecastVersionRequestDto(long? Version = null);

public sealed record ForecastReasonRequestDto(string? Reason, long? Version = null);

public sealed record ForecastNoteRequestDto(string? Note, long? Version = null);

public sealed record ForecastHeaderDto(
    Guid Id, long Version, string PeriodKind, DateOnly PeriodStart, DateOnly PeriodEnd, string ScopeType, Guid? ScopeUserId, string ReportingCurrency,
    int VersionNo, string Status, Guid? PreviousSubmissionId, string? Note, DateTimeOffset? SubmittedAt, Guid? SubmittedBy, DateTimeOffset? LockedAt,
    Guid? LockedBy, string? ReopenReason, string? ReturnReason, decimal? RealizedAtLock, bool RealizedPartial, DateTimeOffset? RefreshedAt, DateTimeOffset? ChangedAt);

public sealed record ForecastItemDto(
    Guid Id, Guid OpportunityId, string OpportunityNumber, string Title, Guid? OwnerId, Guid? CurrentOwnerId, string Stage, decimal? SourceAmount,
    string SourceCurrency, int? Probability, string ProbabilitySource, DateOnly? ExpectedCloseDate, decimal? RateUsed, DateOnly? RateDate, bool RateMissing,
    decimal? BaseAmount, string BaseCategory, string Category, decimal? AdjustmentAmount, decimal? FinalAmount, int? ProbabilityOverride,
    int? EffectiveProbability, decimal? Weighted, IReadOnlyList<string> Flags);

public sealed record ForecastAdjustmentDto(
    Guid Id, Guid? ItemId, Guid OpportunityId, string Kind, string? FromValue, string? ToValue, string Reason, Guid? ChangedBy, DateTimeOffset At);

public sealed record ForecastCategoryTotalDto(string Category, int Count, decimal Amount, decimal Weighted);

public sealed record ForecastScenariosDto(
    decimal Realized, decimal Commit, decimal BestCase, decimal Pipeline, decimal WeightedOpen,
    decimal CommitScenario, decimal OptimisticScenario, decimal WeightedScenario);

public sealed record ForecastVarianceDto(decimal Forecast, decimal Actual, decimal Error, decimal AbsoluteError, decimal? ErrorPercent);

public sealed record ForecastAccuracyDto(string State, int? HorizonDays, ForecastVarianceDto? CommitScenario, ForecastVarianceDto? WeightedScenario, decimal? Actual);

public sealed record ForecastCapabilitiesDto(bool CanWrite, bool CanSubmit, bool CanReturn, bool CanLock, bool CanReopen, bool CanDelete);

public sealed record ForecastDetailDto(
    ForecastHeaderDto Header,
    bool ItemsVisible,
    bool AmountsVisible,
    IReadOnlyList<ForecastItemDto> Items,
    IReadOnlyList<ForecastAdjustmentDto> Adjustments,
    IReadOnlyList<ForecastCategoryTotalDto> ByCategory,
    ForecastScenariosDto? Scenarios,
    decimal? Realized,
    bool RealizedLive,
    ForecastAccuracyDto Accuracy,
    ForecastCapabilitiesDto Capabilities,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions);

public sealed record ForecastCompareEntryDto(
    Guid OpportunityId, string OpportunityNumber, string Title, decimal? BeforeAmount, decimal? AfterAmount, string? BeforeCategory, string? AfterCategory, string? Outcome);

public sealed record ForecastComparisonDto(
    Guid CurrentId, Guid PreviousId, int CurrentVersionNo, int PreviousVersionNo, bool AmountsVisible,
    IReadOnlyList<ForecastCompareEntryDto> Added, IReadOnlyList<ForecastCompareEntryDto> Removed, IReadOnlyList<ForecastCompareEntryDto> AmountChanged,
    IReadOnlyList<ForecastCompareEntryDto> CategoryChanged);

/// <summary>"Benim günüm" öneri türleri (sunucudaki <c>MyDaySuggestionKind</c> ile aynı değerler).</summary>
public static class MyDayKinds
{
    public const string AcceptedQuote = "AcceptedQuote";
    public const string NewLead = "NewLead";
    public const string OverdueOpportunity = "OverdueOpportunity";
    public const string QuoteFollowUp = "QuoteFollowUp";
    public const string StaleOpportunity = "StaleOpportunity";
}

/// <summary>"Benim günüm" önerisi (<c>/api/crm/my-day</c>); Kind: AcceptedQuote, NewLead, OverdueOpportunity, QuoteFollowUp, StaleOpportunity. Metni istemci türe göre kurar.</summary>
public sealed record MyDaySuggestionDto(
    string Kind, Guid RecordId, string Number, string Title, string? Subtitle, string? Stage, decimal? Amount, string Currency,
    int? DaysOverdue, int? DaysSinceContact, int? DaysWaiting, DateTimeOffset? Since);

public sealed record MyDayClosingDto(Guid Id, string Number, string Title, decimal? Amount, string Currency, int? Probability, DateOnly ExpectedCloseDate, bool Overdue);

/// <summary>"Benim günüm" özeti: oturumdaki kullanıcının kendi kayıtları; izni olmayan bölüm boş döner (<c>…Available</c> false).</summary>
public sealed record MyDayDto(
    DateOnly Today,
    DateOnly WeekStart,
    bool AmountsVisible,
    bool OpportunitiesAvailable,
    bool LeadsAvailable,
    bool QuotesAvailable,
    int OpenOpportunityCount,
    int OverdueOpportunityCount,
    int NewLeadCount,
    int QuotesPresentedThisWeek,
    int WonThisWeek,
    int QuoteReminderDays,
    int StaleDays,
    IReadOnlyList<MyDaySuggestionDto> Suggestions,
    IReadOnlyList<MyDayClosingDto> ClosingThisMonth);
