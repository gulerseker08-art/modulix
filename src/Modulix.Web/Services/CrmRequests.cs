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
        o.Probability, o.LostReason, o.OwnerId);

    public static SaveActivityRequest From(ActivityDto a) => new(
        a.Subject, a.Type, a.DueAt, a.CompletedAt, a.CompanyId, a.ContactId, a.OpportunityId, a.Notes, a.Version, a.OwnerId);
}
