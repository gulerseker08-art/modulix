using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Lookups;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0016: lookup ayarı (aranabilir), sunucu tarafı arama, kimlikten çözümleme, erişim kapsamı, bağımlılık ve kayıt doğrulaması.
/// Ayar firmaya aittir; yetkisiz kullanıcı yazamaz; metin araması kapalıyken sunucu reddeder, seçim ve çözümleme çalışır.
/// </summary>
public class LookupTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, ActivitiesRead, ActivitiesWrite];

    private const string ContactCompany = "crm.contact.company";
    private const string OpportunityContact = "crm.opportunity.contact";

    [Fact]
    public async Task Searchable_setting_is_permissioned_versioned_audited_persistent_and_tenant_scoped()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        using var rep = await LoginAsync(a.RepA);
        using var otherAdmin = await LoginAsync(b.Admin);

        var defaults = (await rep.GetFromJsonAsync<List<LookupDto>>("/api/lookups"))!;
        Assert.Equal(26, defaults.Count); // CRM lookup'ları + Core'un "Bildirim gönder" alıcısı (core.notification.recipient)
        var contactCompany = defaults.Single(l => l.Key == ContactCompany);
        Assert.True(contactCompany.Searchable);
        Assert.False(contactCompany.Customized);
        Assert.Equal(0, contactCompany.Version);
        Assert.StartsWith("Kişi", contactCompany.SourceEntityName);
        Assert.StartsWith("Firma", contactCompany.TargetEntityName);

        // Yetkisiz kullanıcı API üzerinden ayarı değiştiremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PutAsJsonAsync(Url(ContactCompany), new SaveLookupSettingRequest(false, 0))).StatusCode);

        var off = await admin.PutAsJsonAsync(Url(ContactCompany), new SaveLookupSettingRequest(false, 0));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var saved = (await off.Content.ReadFromJsonAsync<LookupDto>())!;
        Assert.False(saved.Searchable);
        Assert.True(saved.Customized);
        Assert.Equal(1, saved.Version);

        // Eski sürümle yazım sessizce ezmez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(Url(ContactCompany), new SaveLookupSettingRequest(true, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(Url("crm.uydurma.alan"), new SaveLookupSettingRequest(true, 0))).StatusCode);

        // Yeni oturum (sayfa yenileme/yeniden başlatma): ayar veritabanından gelir; başka firma etkilenmez.
        using var freshRep = await LoginAsync(a.RepA);
        Assert.False((await freshRep.GetFromJsonAsync<List<LookupDto>>("/api/lookups"))!.Single(l => l.Key == ContactCompany).Searchable);
        Assert.True((await otherAdmin.GetFromJsonAsync<List<LookupDto>>("/api/lookups"))!.Single(l => l.Key == ContactCompany).Searchable);

        // Kapalıyken: metin araması sunucuda reddedilir; sayfalı gezinme ve kimlikten çözümleme çalışır.
        var company = await PostAsync<CompanyDto>(freshRep, "/api/crm/companies", new SaveCompanyRequest("Kapalı Arama A.Ş."));
        var blocked = await freshRep.GetAsync(SearchUrl(ContactCompany, "Kapalı"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("search", await blocked.Content.ReadAsStringAsync());
        var browse = (await freshRep.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, null)))!;
        Assert.False(browse.Searchable);
        Assert.Contains(browse.Items, i => i.Id == company.Id);
        var resolved = (await ResolveAsync(freshRep, ContactCompany, company.Id)).Items.Single();
        Assert.Equal("Kapalı Arama A.Ş.", resolved.Label);

        // Yeniden açılınca arama çalışır; "varsayılana dön" satırı silmez, sürüm ilerler.
        var on = (await (await admin.PutAsJsonAsync(Url(ContactCompany), new SaveLookupSettingRequest(null, 1))).Content.ReadFromJsonAsync<LookupDto>())!;
        Assert.True(on.Searchable);
        Assert.False(on.Customized);
        Assert.Equal(2, on.Version);
        var found = (await freshRep.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, "Kapalı")))!;
        Assert.Contains(found.Items, i => i.Id == company.Id);

        var audit = (await admin.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.lookup-setting"))!;
        Assert.Equal(2, audit.Items.Count);
        Assert.All(audit.Items, i => Assert.Equal(a.Admin.Id, i.UserId));
        Assert.Contains(audit.Items, i => i.Changes.Any(c => c.Field == "searchable" && c.New == "Kapalı"));
    }

    [Fact]
    public async Task Search_and_resolve_follow_record_scope_limits_and_distinguish_same_names()
    {
        var t = await SetUpAsync();
        using var repA = await LoginAsync(t.RepA);
        using var repB = await LoginAsync(t.RepB);

        var mine = await PostAsync<CompanyDto>(repA, "/api/crm/companies", new SaveCompanyRequest("Aynı Ad Ltd", City: "Ankara"));
        var twin = await PostAsync<CompanyDto>(repA, "/api/crm/companies", new SaveCompanyRequest("Aynı Ad Ltd", City: "İzmir"));
        var theirs = await PostAsync<CompanyDto>(repB, "/api/crm/companies", new SaveCompanyRequest("Başkasının Firması"));

        // Aynı isimli kayıtlar ikincil bilgiyle (şehir) ayırt edilir.
        var page = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, "Aynı Ad")))!;
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["Ankara", "İzmir"], page.Items.Select(i => i.Secondary).Order());
        Assert.True(page.CanView);

        // Başka kullanıcının kaydı arama, toplam ve kimlikten çözümlemede görünmez (sızıntı yok).
        var leak = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, "Başkasının")))!;
        Assert.Empty(leak.Items);
        Assert.Equal(0, leak.TotalCount);
        var hidden = (await ResolveAsync(repA, ContactCompany, theirs.Id, mine.Id)).Items;
        Assert.True(hidden.Single(i => i.Id == theirs.Id).Unavailable);
        Assert.Null(hidden.Single(i => i.Id == theirs.Id).Label);
        Assert.Equal("Aynı Ad Ltd", hidden.Single(i => i.Id == mine.Id).Label);

        // Sayfa boyutu üst sınırla kısıtlıdır; her sayfa kararlı sırayla gelir.
        for (var i = 0; i < 22; i++)
        {
            await PostAsync<CompanyDto>(repA, "/api/crm/companies", new SaveCompanyRequest($"Toplu {i:00}"));
        }

        var big = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, null, pageSize: 5000)))!;
        Assert.True(big.PageSize <= 50);
        Assert.True(big.Items.Count <= 50);
        var first = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, null)))!;
        var second = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, null, page: 2)))!;
        Assert.Equal(20, first.Items.Count);
        Assert.True(first.TotalCount >= 24);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));

        // Metin sınırları: çok kısa ve çok uzun arama reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repA.GetAsync(SearchUrl(ContactCompany, "a"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repA.GetAsync(SearchUrl(ContactCompany, new string('x', 101)))).StatusCode);

        // Çözümleme de sınırlıdır.
        var tooMany = await repA.PostAsJsonAsync($"/api/lookups/{ContactCompany}/resolve", new ResolveLookupRequest([.. Enumerable.Range(0, 51).Select(_ => Guid.NewGuid())]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
    }

    [Fact]
    public async Task Turkish_characters_and_case_are_matched_by_the_database_collation()
    {
        var t = await SetUpAsync();
        using var rep = await LoginAsync(t.RepA);
        var company = await PostAsync<CompanyDto>(rep, "/api/crm/companies", new SaveCompanyRequest("Çağrı İnşaat Işık Ltd"));

        foreach (var query in new[] { "çağrı", "ÇAĞ", "  Çağrı   İnşaat  " })
        {
            var page = (await rep.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, query)))!;
            Assert.True(page.Items.Any(i => i.Id == company.Id), $"Bulunamadı: '{query}'");
        }

        // Bilinen sınır: SQL_Latin1_General_CP1_CI_AS I/ı ve İ/i çiftlerini eşlemez ("ÇAĞRI" ≠ "çağrı", "inşaat" ≠ "İnşaat"). Üye adı
        // aramasında (tr-TR kültürü) bu çiftler eşleşir; kayıt aramasında harfi aynı yazın. Kolasyon değişirse bu satırlar güncellenmelidir.
        foreach (var query in new[] { "ÇAĞRI", "çağrı inşaat", "IŞIK" })
        {
            var miss = (await rep.GetFromJsonAsync<LookupPageDto>(SearchUrl(ContactCompany, query)))!;
            Assert.DoesNotContain(miss.Items, i => i.Id == company.Id);
        }
    }

    [Fact]
    public async Task Members_lookup_searches_by_name_in_record_scope_and_hides_inactive_members()
    {
        var t = await SetUpAsync();
        using var rep = await LoginAsync(t.RepA);

        var page = (await rep.GetFromJsonAsync<LookupPageDto>(SearchUrl("crm.opportunity.owner", null)))!;
        Assert.Contains(page.Items, i => i.Id == t.RepA.Id);
        Assert.False(page.CanView);

        // Kendi kapsamı dışındaki üye (Own kapsam) sorumlu olarak aranamaz.
        Assert.DoesNotContain(page.Items, i => i.Id == t.RepB.Id);
        var resolved = (await ResolveAsync(rep, "crm.opportunity.owner", t.RepA.Id, Guid.NewGuid())).Items;
        Assert.False(resolved[0].Unavailable);
        Assert.True(resolved[1].Unavailable);
    }

    [Fact]
    public async Task Dependent_lookup_is_filtered_on_the_server_and_invalid_references_are_rejected_on_save()
    {
        var t = await SetUpAsync();
        using var repA = await LoginAsync(t.RepA);
        using var repB = await LoginAsync(t.RepB);

        var acme = await PostAsync<CompanyDto>(repA, "/api/crm/companies", new SaveCompanyRequest("Acme"));
        var globex = await PostAsync<CompanyDto>(repA, "/api/crm/companies", new SaveCompanyRequest("Globex"));
        var alice = await PostAsync<ContactDto>(repA, "/api/crm/contacts", new SaveContactRequest("Alice", "Acme", acme.Id));
        var bob = await PostAsync<ContactDto>(repA, "/api/crm/contacts", new SaveContactRequest("Bob", "Globex", globex.Id));
        var foreign = await PostAsync<CompanyDto>(repB, "/api/crm/companies", new SaveCompanyRequest("Yabancı"));

        // Üst alan verilince sonuçlar sunucuda üst kayda göre süzülür; verilmezse süzülmez.
        var filtered = (await repA.GetFromJsonAsync<LookupPageDto>($"{SearchUrl(OpportunityContact, null)}&parentId={acme.Id}"))!;
        Assert.Equal([alice.Id], filtered.Items.Select(i => i.Id));
        var unfiltered = (await repA.GetFromJsonAsync<LookupPageDto>(SearchUrl(OpportunityContact, null)))!;
        Assert.Contains(unfiltered.Items, i => i.Id == bob.Id);

        // Kayıt: seçim lookup'tan gelmese de aynı kurallar uygulanır.
        var wrongContact = await repA.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Yanlış kişi", acme.Id, bob.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongContact.StatusCode);
        Assert.Contains("contactId", await wrongContact.Content.ReadAsStringAsync());
        var rightContact = await PostAsync<OpportunityDto>(repA, "/api/crm/opportunities", new SaveOpportunityRequest("Doğru kişi", acme.Id, alice.Id));

        // Başka kullanıcının (erişilemeyen) firması ya da var olmayan kayıt kabul edilmez; hata varlığı belli etmez.
        var hiddenCompany = await repA.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Yetkisiz firma", foreign.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hiddenCompany.StatusCode);
        var missingCompany = await repA.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Olmayan firma", Guid.NewGuid()));
        Assert.Equal(await ErrorsOfAsync(hiddenCompany), await ErrorsOfAsync(missingCompany));

        // Güncellemede değişmeyen bağ yeniden doğrulanmaz; değişen bağ doğrulanır.
        var unchanged = await repA.PutAsJsonAsync($"/api/crm/opportunities/{rightContact.Id}",
            new SaveOpportunityRequest("Doğru kişi (güncel)", acme.Id, alice.Id, Version: rightContact.Version, OwnerId: t.RepA.Id));
        Assert.True(unchanged.StatusCode == HttpStatusCode.OK, await unchanged.Content.ReadAsStringAsync());
        var moved = await repA.PutAsJsonAsync($"/api/crm/opportunities/{rightContact.Id}",
            new SaveOpportunityRequest("Doğru kişi", acme.Id, bob.Id, Version: rightContact.Version + 1, OwnerId: t.RepA.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);

        // Aktivite: kişi ve fırsat seçili firmayla ilişkili olmalıdır.
        var activity = await repA.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Uyumsuz", CompanyId: acme.Id, ContactId: bob.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activity.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await repA.PostAsJsonAsync(
            "/api/crm/activities", new SaveActivityRequest("Uyumlu", CompanyId: acme.Id, ContactId: alice.Id, OpportunityId: rightContact.Id))).StatusCode);
    }

    private static async Task<string> ErrorsOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["errors"]!.ToJsonString();

    private static string Url(string key) => $"/api/customization/lookups/{key}";

    private static string SearchUrl(string key, string? text, int page = 1, int? pageSize = null) =>
        $"/api/lookups/{key}/search?page={page}{(pageSize is null ? string.Empty : $"&pageSize={pageSize}")}{(text is null ? string.Empty : $"&q={Uri.EscapeDataString(text)}")}";

    private static async Task<LookupResolveDto> ResolveAsync(HttpClient client, string key, params Guid[] ids)
    {
        var response = await client.PostAsJsonAsync($"/api/lookups/{key}/resolve", new ResolveLookupRequest(ids));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LookupResolveDto>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser RepA, ModulixUser RepB)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        return (admin, repA, repB);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
