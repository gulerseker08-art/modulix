namespace Modulix.Shared.Crm;

// Fırsat yönetimi sözleşmeleri (ADR-0017). Durum ve kapanış değişiklikleri genel PUT ile değil eylem uç noktalarıyla yapılır
// (/api/crm/opportunities/{id}/stage|win|lose|cancel|hold|resume|reopen); her eylem kaydın sürümünü ister.

/// <summary>Fırsat yaşam döngüsü durumu. Aşama kategorisiyle ilişkisi: Kazanıldı ↔ "won", Kaybedildi ↔ "lost"; diğerleri "open" aşamada durur.</summary>
public static class OpportunityStatus
{
    public const string Open = "Open";
    public const string OnHold = "OnHold";
    public const string Won = "Won";
    public const string Lost = "Lost";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Open, OnHold, Won, Lost, Cancelled];

    public static bool IsClosed(string status) => status is Won or Lost or Cancelled;

    public static string Label(string status) => status switch
    {
        Open => "Açık",
        OnHold => "Beklemede",
        Won => "Kazanıldı",
        Lost => "Kaybedildi",
        Cancelled => "İptal edildi",
        _ => status,
    };
}

/// <summary>Müşteri bütçesinin durumu: bilinmeyen bütçe ile bütçenin olmaması aynı şey değildir.</summary>
public static class OpportunityBudgetStatus
{
    public const string Unknown = "Unknown";
    public const string Unconfirmed = "Unconfirmed";
    public const string Confirmed = "Confirmed";
    public const string None = "None";

    public static IReadOnlyList<string> All { get; } = [Unknown, Unconfirmed, Confirmed, None];

    public static string Label(string status) => status switch
    {
        Unknown => "Bilinmiyor",
        Unconfirmed => "Doğrulanmadı",
        Confirmed => "Doğrulandı",
        None => "Bütçe yok",
        _ => status,
    };
}

/// <summary>Fırsat–kişi ilişkisindeki roller.</summary>
public static class OpportunityContactRoles
{
    public static IReadOnlyList<string> All { get; } =
        ["DecisionMaker", "BudgetOwner", "TechnicalEvaluator", "Purchaser", "EndUser", "Champion", "Other"];

    public static string Label(string role) => role switch
    {
        "DecisionMaker" => "Karar verici",
        "BudgetOwner" => "Bütçe sahibi",
        "TechnicalEvaluator" => "Teknik değerlendirici",
        "Purchaser" => "Satın almacı",
        "EndUser" => "Son kullanıcı",
        "Champion" => "İç destekçi",
        "Other" => "Diğer",
        _ => role,
    };
}

public sealed record OpportunityContactDto(
    Guid Id, long Version, Guid OpportunityId, Guid ContactId, string? ContactName, IReadOnlyList<string> Roles, bool IsPrimary, string? Note);

public sealed record SaveOpportunityContactRequest(
    Guid OpportunityId, Guid ContactId, IReadOnlyList<string>? Roles = null, bool IsPrimary = false, string? Note = null, long? Version = null);

public sealed record OpportunityCompetitorDto(
    Guid Id, long Version, Guid OpportunityId, string Name, string? Strengths, string? Weaknesses, decimal? AlternativeAmount,
    string? AlternativeCurrency, string? Notes);

public sealed record SaveOpportunityCompetitorRequest(
    Guid OpportunityId, string Name, string? Strengths = null, string? Weaknesses = null, decimal? AlternativeAmount = null,
    string? AlternativeCurrency = null, string? Notes = null, long? Version = null);

// ---- Eylemler

public sealed record ChangeOpportunityStageRequest(long Version, string Stage, string? Note = null);

public sealed record WinOpportunityRequestDto(
    long Version, DateOnly? ActualCloseDate = null, decimal? WonAmount = null, string? WonCurrency = null, string? Reason = null,
    string? HandoverNote = null, string? Stage = null);

public sealed record LoseOpportunityRequestDto(
    long Version, string Reason, string? Note = null, Guid? WinningCompetitorId = null, DateOnly? RecontactDate = null, bool CreateFollowUp = false);

public sealed record CancelOpportunityRequest(long Version, string Reason);

public sealed record HoldOpportunityRequest(long Version, string Reason, DateOnly ReviewDate);

public sealed record ResumeOpportunityRequest(long Version, string? Note = null);

public sealed record ReopenOpportunityRequestDto(long Version, string Reason, string Stage, DateOnly ExpectedCloseDate, int? Probability = null);

/// <summary>Eylem sonucu. <see cref="OpenActivities"/>: kapanıştan sonra fırsata bağlı açık aktivite sayısı (silinmez, açık kalır).</summary>
public sealed record OpportunityActionResultDto(
    OpportunityDto Opportunity, int OpenActivities, Guid? FollowUpActivityId, string? FollowUpWarning);

public sealed record OpportunityHistoryDto(
    Guid Id, string Kind, DateTimeOffset At, Guid? ChangedBy, int Cycle, string? FromStage, string? ToStage, string? FromStatus,
    string? ToStatus, DateOnly? FromCloseDate, DateOnly? ToCloseDate, string? Reason, string? Details);

public sealed record OpportunitySettingsDto(int StaleDays);

// ---- Rapor

/// <summary>Para birimi bazında toplam. Farklı para birimleri toplanmaz; kur dönüşümü yoktur.</summary>
public sealed record OpportunityCurrencyTotal(string Currency, int Count, decimal? Amount, decimal? Weighted, int UnknownAmountCount, int NoProbabilityCount);

public sealed record StageSummary(string Stage, int Count, IReadOnlyList<OpportunityCurrencyTotal> Totals, double? AverageDaysInStage);

public sealed record OwnerSummary(Guid? OwnerId, int OpenCount, int WonCount, int LostCount, IReadOnlyList<OpportunityCurrencyTotal> OpenTotals);

public sealed record MonthForecast(string Month, IReadOnlyList<OpportunityCurrencyTotal> Totals);

public sealed record ReasonCount(string Key, int Count);

public sealed record CompetitorLoss(Guid CompetitorId, string? Name, int Count);

public sealed record PostponedOpportunity(Guid Id, string Title, int ChangeCount, DateOnly? ExpectedCloseDate);

public sealed record AttentionItem(Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, DateOnly? Date);

/// <summary>
/// Fırsat raporu. Tüm değerler çağıranın izin ve veri kapsamındaki fırsatlardan hesaplanır (kayıt boru hattı); tutar izni yoksa
/// tutar alanları null döner. Rapor bir istekte en fazla 5000 fırsat okur (<see cref="Truncated"/>). Tanımlar <see cref="Definitions"/>'tadır.
/// </summary>
public sealed record OpportunityReportDto(
    DateOnly From,
    DateOnly To,
    bool Truncated,
    bool AmountsVisible,
    int OpenCount,
    int OnHoldCount,
    IReadOnlyList<OpportunityCurrencyTotal> OpenTotals,
    IReadOnlyList<StageSummary> ByStage,
    IReadOnlyList<OwnerSummary> ByOwner,
    IReadOnlyList<MonthForecast> Forecast,
    int WonCount,
    int LostCount,
    int CancelledCount,
    double? WinRate,
    IReadOnlyList<OpportunityCurrencyTotal> WonTotals,
    double? AverageCycleDays,
    IReadOnlyList<ReasonCount> LossReasons,
    IReadOnlyList<CompetitorLoss> CompetitorLosses,
    IReadOnlyList<AttentionItem> Overdue,
    IReadOnlyList<AttentionItem> NoNextAction,
    IReadOnlyList<AttentionItem> Inactive,
    IReadOnlyList<PostponedOpportunity> Postponed,
    IReadOnlyList<string> Definitions);
