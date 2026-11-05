using System.Globalization;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;

namespace Modulix.Web.Services;

/// <summary>
/// Seçim listeleri için firma, kişi ve fırsat adları ile kuruluş üyeleri (sorumlu seçimi). v1: her türden ilk 200 kayıt (API'nin sayfa sınırı);
/// daha fazlası için arama destekli seçim kutusu gerekir. Okuma izni yoksa liste boş kalır.
/// </summary>
public sealed class CrmLookups(ApiClient api, SessionState session)
{
    private const int Size = 200;

    // Atanabilir üye listesi şimdilik CRM genelinde tek listedir; kapsam henüz türe göre değişmediği için (ADR-0030 dilim 1)
    // hangi tür anahtarının sorulduğu sonucu etkilemez. Türe özel kapsam gelince form türüne göre ayrı istenmelidir (dilim 3).
    private const string AssignableEntityKey = CrmEntityKeys.Opportunity;

    public IReadOnlyList<CompanyDto> Companies { get; private set; } = [];

    public IReadOnlyList<ContactDto> Contacts { get; private set; } = [];

    public IReadOnlyList<OpportunityDto> Opportunities { get; private set; } = [];

    public IReadOnlyList<TenantMemberResponse> Members { get; private set; } = [];

    /// <summary>Sorumlu olarak atanabilecek üyeler: kişinin kayıt kapsamındakiler (Y8). Adlar için <see cref="Members"/> kullanılır.</summary>
    public IReadOnlyList<TenantMemberResponse> AssignableMembers { get; private set; } = [];

    /// <summary>Firmanın özel alanları (Y9a), tanım sırasıyla; formlar, listeler ve ayrıntılar bunlarla çizilir.</summary>
    public IReadOnlyList<CustomFieldDto> CustomFields { get; private set; } = [];

    public IEnumerable<CustomFieldDto> FieldsOf(string entityKey) => CustomFields.Where(f => f.EntityKey == entityKey);

    public IEnumerable<CustomFieldDto> ListFieldsOf(string entityKey) => FieldsOf(entityKey).Where(f => f.ShowInList);

    /// <summary>Firmanın seçim listeleri (Y9b): aşama, durum, kaynak, aktivite tipi; sırası ve adları firmanındır.</summary>
    public IReadOnlyList<ChoiceListDto> ChoiceLists { get; private set; } = [];

    /// <summary>Seçilebilecek değerler: etkinler ve (pasifse) kaydın mevcut değeri.</summary>
    public IEnumerable<ChoiceValueDto> Choices(string listKey, string? current = null) =>
        ValuesOf(listKey).Where(v => v.Active || v.Key == current);

    public IReadOnlyList<ChoiceValueDto> ValuesOf(string listKey) => ChoiceLists.FirstOrDefault(l => l.Key == listKey)?.Values ?? [];

    /// <summary>Değerin adı; listede yoksa (liste yüklenmeden) anahtarın kendisi.</summary>
    public string Label(string listKey, string? key) =>
        key is null ? string.Empty : ValuesOf(listKey).FirstOrDefault(v => v.Key == key)?.Label ?? key;

    /// <summary>Yeni kayıtta önerilen değer: listenin varsayılanı.</summary>
    public string? DefaultOf(string listKey) => ChoiceLists.FirstOrDefault(l => l.Key == listKey)?.Default;

    public string? CategoryOf(string listKey, string? key) => ValuesOf(listKey).FirstOrDefault(v => v.Key == key)?.Category;

    public bool IsWon(string stage) => CategoryOf(CrmLists.OpportunityStage, stage) == CrmLists.StageCategories.Won;

    public bool IsLost(string stage) => CategoryOf(CrmLists.OpportunityStage, stage) == CrmLists.StageCategories.Lost;

    public bool IsClosed(string stage) => IsWon(stage) || IsLost(stage);

    /// <summary>Rozet rengi: hazır değerin kendi rengi, firmanın eklediği değerde kategorisinin rengi.</summary>
    public string Badge(string listKey, string? key)
    {
        var value = ValuesOf(listKey).FirstOrDefault(v => v.Key == key);
        return value is null or { BuiltIn: true }
            ? (key ?? string.Empty).ToLowerInvariant()
            : value.Category == CrmLists.StageCategories.Open ? "qualified" : value.Category ?? "custom";
    }

    public async Task LoadAsync(bool contacts = false, bool opportunities = false)
    {
        CustomFields = (await api.GetAsync<List<CustomFieldDto>>("/api/custom-fields")).Value ?? [];
        ChoiceLists = (await api.GetAsync<List<ChoiceListDto>>("/api/choice-lists")).Value ?? [];
        Members = (await api.GetAsync<List<TenantMemberResponse>>("/api/tenant/members")).Value ?? [];
        AssignableMembers = session.Scope == RecordScope.All && session.Access is not null
            ? Members
            : (await api.GetAsync<List<TenantMemberResponse>>($"/api/tenant/members?assignable=true&entityKey={AssignableEntityKey}")).Value ?? [];

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

/// <summary>Sayı, tarih ve etiket biçimleri. Seçim değerlerinin adları firmanın listesindedir (<see cref="CrmLookups.Label"/>).</summary>
public static class CrmLabels
{
    // Biçimler ve saat dilimi firmanın bölgesel ayarlarından gelir (Y6b, Regional).
    public static string Amount(decimal amount, string currency) => $"{amount.ToString("N2", Regional.Numbers)} {currency}";

    public static string Date(DateOnly date) => date.ToString(Regional.DatePattern, CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset time) =>
        Regional.ToTenant(time).ToString($"{Regional.DatePattern} {Regional.TimePattern}", CultureInfo.InvariantCulture);

    /// <summary>Etiket metnini ("a, b") ayrı etiketlere böler.</summary>
    public static IEnumerable<string> Tags(string? tags) =>
        (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
