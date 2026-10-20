using System.Globalization;
using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Seçim listeleri için firma, kişi ve fırsat adları. v1: her türden ilk 200 kayıt (API'nin sayfa sınırı);
/// daha fazlası için arama destekli seçim kutusu gerekir. Okuma izni yoksa liste boş kalır.
/// </summary>
public sealed class CrmLookups(ApiClient api, SessionState session)
{
    private const int Size = 200;

    public IReadOnlyList<CompanyDto> Companies { get; private set; } = [];

    public IReadOnlyList<ContactDto> Contacts { get; private set; } = [];

    public IReadOnlyList<OpportunityDto> Opportunities { get; private set; } = [];

    public async Task LoadAsync(bool contacts = false, bool opportunities = false)
    {
        if (session.Can(CrmAccess.CompaniesRead))
        {
            Companies = (await api.GetAsync<CrmPage<CompanyDto>>($"/api/crm/companies?pageSize={Size}")).Value?.Items ?? [];
        }

        if (contacts && session.Can(CrmAccess.ContactsRead))
        {
            Contacts = (await api.GetAsync<CrmPage<ContactDto>>($"/api/crm/contacts?pageSize={Size}")).Value?.Items ?? [];
        }

        if (opportunities && session.Can(CrmAccess.OpportunitiesRead))
        {
            Opportunities = (await api.GetAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?pageSize={Size}")).Value?.Items ?? [];
        }
    }

    public string CompanyName(Guid? id) => id is null ? string.Empty : Companies.FirstOrDefault(c => c.Id == id)?.Name ?? "—";

    public string ContactName(Guid? id) =>
        id is null ? string.Empty : Contacts.FirstOrDefault(c => c.Id == id) is { } c ? $"{c.FirstName} {c.LastName}" : "—";

    public string OpportunityTitle(Guid? id) => id is null ? string.Empty : Opportunities.FirstOrDefault(o => o.Id == id)?.Title ?? "—";
}

/// <summary>Seçeneklerin Türkçe adları ve sayı/tarih biçimleri.</summary>
public static class CrmLabels
{
    // Tarayıcıda tüm kültür verisi yüklenmez (indirme boyutu); Türkçe biçim burada sabitlenir.
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    public static string Amount(decimal amount, string currency) => $"{amount.ToString("N2", Numbers)} {currency}";

    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Of(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Lead => "Aday",
        OpportunityStage.Qualified => "Nitelikli",
        OpportunityStage.Proposal => "Teklif",
        OpportunityStage.Negotiation => "Pazarlık",
        OpportunityStage.Won => "Kazanıldı",
        OpportunityStage.Lost => "Kaybedildi",
        _ => stage.ToString(),
    };

    public static string Of(ActivityType type) => type switch
    {
        ActivityType.Call => "Arama",
        ActivityType.Meeting => "Toplantı",
        ActivityType.Email => "E-posta",
        ActivityType.Task => "Görev",
        ActivityType.Note => "Not",
        _ => type.ToString(),
    };
}
