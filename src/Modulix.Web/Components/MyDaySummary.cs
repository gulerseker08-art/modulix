namespace Modulix.Web.Components;

/// <summary>"Benim günüm" selamlama satırının sayıları: bugünkü açık iş ve kapanış tarihi geçmiş fırsat; bilinmiyorsa null.</summary>
public sealed record MyDaySummary(int? OpenToday, int? OverdueOpportunities);
