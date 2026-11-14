using System.Globalization;
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

    // Atanabilir üye listesi aktivitenin "Yeniden ata" eylemi içindir. Kapsam kayıt türüne göre (istisna ve platform tavanı dahil)
    // çözüldüğü için aktivitenin kendi türüyle sorulur; oturumdaki tek varsayılan kapsamdan çıkarım yapılmaz (ADR-0030).
    private const string AssignableEntityKey = CrmEntityKeys.Activity;

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

    // Aynı örnekte sürmekte olan yüklemeler (ör. ana sayfa iki bölüm için aynı anda ister): ikinci çağrı yeni istek atmaz,
    // sürenini bekler. Biten yükleme listeden çıkar; sonraki çağrı (kayıttan sonra yenileme) yine sunucudan okur.
    private readonly Dictionary<(bool Contacts, bool Opportunities), Task> running = [];

    public Task LoadAsync(bool contacts = false, bool opportunities = false)
    {
        var key = (contacts, opportunities);
        if (running.TryGetValue(key, out var pending))
        {
            return pending;
        }

        var task = LoadCoreAsync(contacts, opportunities);
        if (!task.IsCompleted)
        {
            running[key] = task;
            _ = task.ContinueWith(_ => running.Remove(key), TaskScheduler.Default);
        }

        return task;
    }

    // İstekler birbirinden bağımsızdır; sırayla değil birlikte gönderilir (her biri sunucuda ayrı yetki/kapsam sorgusu taşır).
    private async Task LoadCoreAsync(bool contacts, bool opportunities)
    {
        var customFields = api.GetAsync<List<CustomFieldDto>>("/api/custom-fields");
        var choiceLists = api.GetAsync<List<ChoiceListDto>>("/api/choice-lists");
        var members = api.GetAsync<List<TenantMemberResponse>>("/api/tenant/members");
        var assignable = api.GetAsync<List<TenantMemberResponse>>($"/api/tenant/members?assignable=true&entityKey={AssignableEntityKey}");
        var companies = session.Can(CrmAccess.CompaniesRead)
            ? api.GetAsync<CrmPage<CompanyDto>>($"/api/crm/companies?pageSize={Size}")
            : null;
        var contactPage = contacts && session.Can(CrmAccess.ContactsRead)
            ? api.GetAsync<CrmPage<ContactDto>>($"/api/crm/contacts?pageSize={Size}")
            : null;
        var opportunityPage = opportunities && session.Can(CrmAccess.OpportunitiesRead)
            ? api.GetAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?pageSize={Size}")
            : null;

        CustomFields = (await customFields).Value ?? [];
        ChoiceLists = (await choiceLists).Value ?? [];
        Members = (await members).Value ?? [];
        AssignableMembers = (await assignable).Value ?? [];

        if (companies is not null)
        {
            Companies = (await companies).Value?.Items ?? [];
        }

        if (contactPage is not null)
        {
            Contacts = (await contactPage).Value?.Items ?? [];
        }

        if (opportunityPage is not null)
        {
            Opportunities = (await opportunityPage).Value?.Items ?? [];
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
