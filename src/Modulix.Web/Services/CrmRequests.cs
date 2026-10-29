using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Kaydın tamamını yazan PUT istekleri için DTO → istek dönüşümü. Tek alan değişen hızlı işlemler
/// (aşama taşıma, aktiviteyi tamamlama) diğer alanları olduğu gibi gönderir; sürüm eşzamanlılığı korur.
/// </summary>
public static class CrmRequests
{
    public static SaveOpportunityRequest From(OpportunityDto o) => new(
        o.Title, o.CompanyId, o.ContactId, o.Stage, o.Amount, o.Currency, o.ExpectedCloseDate, o.Notes, o.Version,
        o.Probability, o.LostReason, o.OwnerId,
        Description: o.Description, Priority: o.Priority, Type: o.Type, Source: o.Source, CustomerBudget: o.CustomerBudget, BudgetStatus: o.BudgetStatus,
        NeedDescription: o.NeedDescription, CurrentProblem: o.CurrentProblem, ExpectedBenefit: o.ExpectedBenefit, Scope: o.Scope,
        SuccessCriteria: o.SuccessCriteria, Risks: o.Risks, DecisionProcess: o.DecisionProcess, TargetDecisionDate: o.TargetDecisionDate,
        NextAction: o.NextAction, NextActionDate: o.NextActionDate);

    public static SaveActivityRequest From(ActivityDto a) => new(
        a.Subject, a.Type, a.DueAt, a.CompletedAt, a.CompanyId, a.ContactId, a.OpportunityId, a.Notes, a.Version, a.OwnerId,
        LeadId: a.LeadId, Outcome: a.Outcome, Priority: a.Priority, StartAt: a.StartAt, EndAt: a.EndAt, IsAllDay: a.IsAllDay,
        StartDate: a.StartDate, EndDate: a.EndDate, TimeZone: a.TimeZone, Location: a.Location, OnlineMeetingUrl: a.OnlineMeetingUrl,
        IsPrivate: a.IsPrivate, EstimatedMinutes: a.EstimatedMinutes, ActualMinutes: a.ActualMinutes);
}
