namespace Modulix.Shared.Crm;

// Teklif raporu sözleşmeleri (ADR-0019, dilim 6). Alan adları sunucudaki rapor sonucuyla birebir aynıdır. Kalem maliyeti ve iç not içermez.

public sealed record QuoteCurrencyTotalDto(string Currency, int Count, decimal Gross, decimal Net, decimal Discount);

public sealed record QuoteReportOwnerDto(Guid? OwnerId, int Presented, int Accepted, int Rejected, IReadOnlyList<QuoteCurrencyTotalDto> AcceptedTotals);

public sealed record QuoteReportItemDto(Guid Id, string QuoteNumber, int RevisionNo, string Title, Guid? OwnerId, string Currency, decimal Gross, DateOnly? Date, string? Note);

public sealed record QuoteReportDto(
    DateOnly From,
    DateOnly To,
    bool Truncated,
    int DraftCount,
    int PendingApprovalCount,
    int WaitingCount,
    IReadOnlyList<QuoteCurrencyTotalDto> WaitingTotals,
    int PresentedCount,
    IReadOnlyList<QuoteCurrencyTotalDto> PresentedTotals,
    int AcceptedCount,
    IReadOnlyList<QuoteCurrencyTotalDto> AcceptedTotals,
    int RejectedCount,
    int CancelledCount,
    int ExpiredCount,
    double? AcceptanceRate,
    double? AverageDaysToDecision,
    double? AverageDiscountPercent,
    double? AverageRevisionsToAccept,
    IReadOnlyList<QuoteReportOwnerDto> ByOwner,
    IReadOnlyList<QuoteReportItemDto> ExpiringSoon,
    IReadOnlyList<QuoteReportItemDto> Overdue,
    IReadOnlyList<QuoteReportItemDto> AwaitingApproval,
    IReadOnlyList<QuoteReportItemDto> NotDelivered,
    IReadOnlyList<string> Definitions);

// Marj raporu (yalnızca crm.quote-costs.read). Alan adları sunucudaki QuoteMarginReportResult ile aynıdır.
public sealed record QuoteMarginTotalDto(string Currency, int Count, decimal Net, decimal Cost, decimal Margin, double? MarginPercent, int LinesWithoutCost);

public sealed record QuoteMarginOwnerDto(Guid? OwnerId, IReadOnlyList<QuoteMarginTotalDto> Totals);

public sealed record QuoteMarginItemDto(Guid Id, string QuoteNumber, int RevisionNo, string Title, Guid? OwnerId, string Status, string Currency, decimal Net, decimal Cost, decimal Margin, double? MarginPercent, int LinesWithoutCost);

public sealed record QuoteMarginReportDto(
    DateOnly From, DateOnly To, bool Truncated, IReadOnlyList<QuoteMarginTotalDto> PresentedTotals, IReadOnlyList<QuoteMarginTotalDto> AcceptedTotals,
    IReadOnlyList<QuoteMarginOwnerDto> ByOwner, IReadOnlyList<QuoteMarginItemDto> LowestMargin, IReadOnlyList<QuoteMarginItemDto> MissingCost, IReadOnlyList<string> Definitions);
