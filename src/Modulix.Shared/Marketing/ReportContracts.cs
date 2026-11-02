namespace Modulix.Shared.Marketing;

// Kampanya raporu ve müşteri kartı geçmişi (ADR-0025, dilim 8). "Gönderildi" = sağlayıcının kabul ettiği + sonucu belirsiz iletiler; teslim edildi anlamına gelmez.
// İlk dokunuş: lead açılan hedefe giden en eski ileti bu kampanyadandı. Son dokunuş: lead'in açıldığı yanıt bu kampanyanın iletisine verilmişti.

public sealed record CampaignReportRowDto(
    Guid CampaignId, string Code, string Name, string Channel, string Status, int Runs,
    int Sent, int Accepted, int Unknown, int Failed, int Skipped,
    int Responses, int Interested, int OptOuts, int Complaints, int Leads, int FirstTouchLeads, int LastTouchLeads,
    decimal? Budget, decimal? ActualCost, string Currency, decimal? CostPerResponse, decimal? CostPerLead);

public sealed record CampaignReportDto(IReadOnlyList<CampaignReportRowDto> Rows, DateTimeOffset GeneratedAt, bool Truncated);

public sealed record TouchHistoryItemDto(string Kind, Guid CampaignId, string CampaignName, DateTimeOffset At, string Status, string? Detail);

public sealed record TargetHistoryDto(IReadOnlyList<TouchHistoryItemDto> Items);
