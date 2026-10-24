using System.Globalization;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Seçim listeleri için firma, kişi ve fırsat adları ile kuruluş üyeleri (sorumlu seçimi). v1: her türden ilk 200 kayıt (API'nin sayfa sınırı);
/// daha fazlası için arama destekli seçim kutusu gerekir. Okuma izni yoksa liste boş kalır.
/// </summary>
public sealed class CrmLookups(ApiClient api, SessionState session)
{
    private const int Size = 200;

    public IReadOnlyList<CompanyDto> Companies { get; private set; } = [];

    public IReadOnlyList<ContactDto> Contacts { get; private set; } = [];

    public IReadOnlyList<OpportunityDto> Opportunities { get; private set; } = [];

    public IReadOnlyList<TenantMemberResponse> Members { get; private set; } = [];

    /// <summary>Sorumlu olarak atanabilecek üyeler: kişinin kayıt kapsamındakiler (Y8). Adlar için <see cref="Members"/> kullanılır.</summary>
    public IReadOnlyList<TenantMemberResponse> AssignableMembers { get; private set; } = [];

    public async Task LoadAsync(bool contacts = false, bool opportunities = false)
    {
        Members = (await api.GetAsync<List<TenantMemberResponse>>("/api/tenant/members")).Value ?? [];
        AssignableMembers = session.Scope == RecordScope.All && session.Access is not null
            ? Members
            : (await api.GetAsync<List<TenantMemberResponse>>("/api/tenant/members?assignable=true")).Value ?? [];

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

    public string MemberName(Guid? id) => id is null ? string.Empty : Members.FirstOrDefault(m => m.UserId == id)?.DisplayName ?? "—";

    public string OpportunityTitle(Guid? id) => id is null ? string.Empty : Opportunities.FirstOrDefault(o => o.Id == id)?.Title ?? "—";
}

/// <summary>Seçeneklerin Türkçe adları ve sayı/tarih biçimleri.</summary>
public static class CrmLabels
{
    // Biçimler ve saat dilimi firmanın bölgesel ayarlarından gelir (Y6b, Regional).
    public static string Amount(decimal amount, string currency) => $"{amount.ToString("N2", Regional.Numbers)} {currency}";

    public static string Date(DateOnly date) => date.ToString(Regional.DatePattern, CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset time) =>
        Regional.ToTenant(time).ToString($"{Regional.DatePattern} {Regional.TimePattern}", CultureInfo.InvariantCulture);

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

    public static string Of(CompanyStatus status) => status switch
    {
        CompanyStatus.Prospect => "Potansiyel",
        CompanyStatus.Customer => "Müşteri",
        CompanyStatus.Inactive => "Pasif",
        _ => status.ToString(),
    };

    public static string Of(CompanySource source) => source switch
    {
        CompanySource.Referral => "Tavsiye",
        CompanySource.Website => "Web sitesi",
        CompanySource.Event => "Etkinlik / fuar",
        CompanySource.Advertising => "Reklam",
        CompanySource.ColdCall => "Soğuk arama",
        CompanySource.Other => "Diğer",
        _ => source.ToString(),
    };

    /// <summary>Etiket metnini ("a, b") ayrı etiketlere böler.</summary>
    public static IEnumerable<string> Tags(string? tags) =>
        (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
