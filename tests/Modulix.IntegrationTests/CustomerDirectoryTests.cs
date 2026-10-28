using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0012: müşteri lokasyonları, yapılandırılmış adresler, çoklu iletişim noktaları ve firma–firma ilişkileri; eski tekil
/// alanlarla (telefon, e-posta, şehir, adres) uyum, normalizasyon ve veri kapsamı.
/// </summary>
public class CustomerDirectoryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Locations = "/api/crm/locations";
    private const string Addresses = "/api/crm/addresses";
    private const string Points = "/api/crm/contact-points";
    private const string PersonPoints = "/api/crm/person-contact-points";
    private const string Relations = "/api/crm/company-relations";

    private static readonly string[] SalesAccess =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, ActivitiesRead, ActivitiesWrite,
    ];

    [Fact]
    public async Task Location_of_the_same_legal_entity_does_not_open_a_new_party_but_a_separate_entity_is_a_separate_company()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Lokasyonlu A.Ş.");
        var before = (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount;

        var depot = await CreateAsync<LocationDto>(client, Locations, new SaveLocationRequest(company.Id, "İstanbul deposu", "IST-DEPO", "Warehouse"));
        var plant = await CreateAsync<LocationDto>(client, Locations, new SaveLocationRequest(company.Id, "Gebze fabrika", "GEB", "Plant"));

        // Lokasyon firma kaydı/ortak taraf açmaz; işletmecinin kendi şube listesi de etkilenmez.
        Assert.Equal(before, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);
        Assert.Equal("Lokasyonlu A.Ş.", depot.CompanyName);
        var list = (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"{Locations}?companyId={company.Id}"))!;
        Assert.Equal(["Gebze fabrika", "İstanbul deposu"], list.Items.Select(l => l.Name));
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"{Locations}?companyId={company.Id}&type=Plant"))!.TotalCount);

        // Kod firma içinde tekildir; başka firmada aynı kod olabilir. Geçersiz tür reddedilir.
        var duplicate = await client.PostAsJsonAsync(Locations, new SaveLocationRequest(company.Id, "Başka", "IST-DEPO"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("code", await duplicate.Content.ReadAsStringAsync());
        var other = await CreateCompanyAsync(client, "Diğer A.Ş.");
        await CreateAsync<LocationDto>(client, Locations, new SaveLocationRequest(other.Id, "Aynı kod", "IST-DEPO"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(Locations, new SaveLocationRequest(company.Id, "Tür", Type: "Moon"))).StatusCode);

        // Ayrı tüzel kişilik ayrı firmadır ve firma–firma ilişkisiyle bağlanır; lokasyon sayısı değişmez.
        var subsidiary = await CreateCompanyAsync(client, "Bağlı Ltd.");
        var relation = await CreateAsync<CompanyRelationDto>(client, Relations, new SaveCompanyRelationRequest(company.Id, subsidiary.Id));
        Assert.Equal("Bağlı Ltd.", relation.ChildCompanyName);
        Assert.Equal(2, (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"{Locations}?companyId={company.Id}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"{Locations}?companyId={subsidiary.Id}"))!.TotalCount);

        // Lokasyon kaydında firma değiştirilemez.
        var moved = await client.PutAsJsonAsync($"{Locations}/{plant.Id}",
            new SaveLocationRequest(other.Id, plant.Name, plant.Code, plant.Type, Version: plant.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);
    }

    [Fact]
    public async Task Addresses_are_structured_searchable_without_diacritics_and_have_one_primary_per_owner()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Adresli A.Ş.");
        var location = await CreateAsync<LocationDto>(client, Locations, new SaveLocationRequest(company.Id, "Şube"));

        var head = await CreateAsync<AddressDto>(client, Addresses, new SaveAddressRequest(
            company.Id, "Büyükdere Cad. No:1", Type: "Registered", IsPrimary: true, City: "İstanbul", District: "Şişli", PostalCode: "34394"));
        Assert.Equal("TR", head.Country);
        Assert.Equal("Şişli", head.District);   // orijinal değer korunur
        Assert.Contains("sisli", head.NormalizedText);

        // Arama aksansız yapılır.
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<AddressDto>>($"{Addresses}?companyId={company.Id}&search=sisli"))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<AddressDto>>($"{Addresses}?companyId={company.Id}&search=ISTANBUL"))!.TotalCount);

        // İkinci firma düzeyi ana adres reddedilir; lokasyonun kendi ana adresi ayrıdır.
        var second = await client.PostAsJsonAsync(Addresses, new SaveAddressRequest(company.Id, "Başka cadde", IsPrimary: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("isPrimary", await second.Content.ReadAsStringAsync());
        var branch = await CreateAsync<AddressDto>(client, Addresses, new SaveAddressRequest(company.Id, "Şube sok.", location.Id, "Shipping", true, Country: "de", City: "Berlin"));
        Assert.Equal("DE", branch.Country);

        // Geçersiz ülke, başka firmanın lokasyonu ve boş satır reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Addresses, new SaveAddressRequest(company.Id, "x", Country: "TUR"))).StatusCode);
        var other = await CreateCompanyAsync(client, "Yabancı A.Ş.");
        var foreignLocation = await client.PostAsJsonAsync(Addresses, new SaveAddressRequest(other.Id, "x", location.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignLocation.StatusCode);
        Assert.Contains("locationId", await foreignLocation.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Addresses, new SaveAddressRequest(company.Id, " "))).StatusCode);

        // Eski alanlar ana adresin aynasıdır.
        var legacy = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal("Büyükdere Cad. No:1", legacy.Address);
        Assert.Equal("İstanbul", legacy.City);

        // Eşzamanlı: yeni firmada aynı anda iki ana adres isteyen iki istekten yalnızca biri geçer.
        var fresh = await CreateCompanyAsync(client, "Yarış A.Ş.");
        var race = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            client.PostAsJsonAsync(Addresses, new SaveAddressRequest(fresh.Id, $"Adres {i}", IsPrimary: true))));
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(race.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Contains((int)r.StatusCode, new[] { 409, 422 }));
    }

    [Fact]
    public async Task Contact_points_keep_the_original_value_normalize_for_search_and_allow_shared_values()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var first = await CreateCompanyAsync(client, "Birinci A.Ş.");
        var second = await CreateCompanyAsync(client, "İkinci A.Ş.");

        var phone = await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(first.Id, "0532 123 45 67", "Phone", Type: "Mobile", IsPrimary: true));
        Assert.Equal("0532 123 45 67", phone.Value);
        Assert.Equal("+905321234567", phone.NormalizedValue);

        // Farklı biçimde yazılan aynı numara aramada bulunur.
        foreach (var term in new[] { "+90 532 123 45 67", "532-123-4567", "00905321234567" })
        {
            var found = await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?search={Uri.EscapeDataString(term)}");
            Assert.Equal(phone.Id, Assert.Single(found!.Items).Id);
        }

        // Aynı sahipte aynı kanalda tekrar engellenir; farklı sahipte aynı santral/e-posta serbesttir (kimlik kanıtı değildir).
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(Points, new SaveContactPointRequest(first.Id, "+90 (532) 123-45-67"))).StatusCode);
        var shared1 = await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(first.Id, "info@grup.com", "Email", IsShared: true));
        var shared2 = await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(second.Id, "INFO@grup.com", "Email", IsShared: true));
        Assert.Equal(shared1.NormalizedValue, shared2.NormalizedValue);
        Assert.Equal(2, (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?search=info@grup.com"))!.TotalCount);

        // Ana nokta sahip + kanal başına tektir.
        var primaryTwice = await client.PostAsJsonAsync(Points, new SaveContactPointRequest(first.Id, "0212 555 00 00", IsPrimary: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, primaryTwice.StatusCode);
        Assert.Contains("isPrimary", await primaryTwice.Content.ReadAsStringAsync());
        var race = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            client.PostAsJsonAsync(Points, new SaveContactPointRequest(second.Id, $"0216 555 00 0{i}", IsPrimary: true))));
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.Created));

        // Geçersiz telefon ve e-posta reddedilir; kanal listesi dışı değer de.
        foreach (var (value, channel) in new[] { ("abc", "Phone"), ("123", "Phone"), ("x@", "Email"), ("0532", "Fax") })
        {
            var bad = await client.PostAsJsonAsync(Points, new SaveContactPointRequest(first.Id, value, channel));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        }

        // İletişim noktası pazarlama izni değildir: sözleşmede böyle bir alan yoktur.
        Assert.DoesNotContain(typeof(ContactPointDto).GetProperties(), p => p.Name.Contains("consent", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("marketing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Person_contact_points_can_belong_to_a_relationship_and_the_same_email_may_belong_to_two_people()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Kişili A.Ş.");
        var ayse = await CreateContactAsync(client, "Ayşe", "Bir", email: "ortak@kutu.com");
        var mehmet = await CreateContactAsync(client, "Mehmet", "İki", email: "ortak@kutu.com");   // aynı e-posta, farklı kişi

        // Eski e-posta alanı kişinin genel ana noktasıdır; iki kişide de aynı normalleştirilmiş değer vardır.
        var found = (await client.GetFromJsonAsync<CrmPage<PersonContactPointDto>>($"{PersonPoints}?search=ortak@kutu.com"))!;
        Assert.Equal(2, found.TotalCount);
        Assert.Equal([ayse.Id, mehmet.Id], found.Items.Select(p => p.ContactId).Order());

        var link = await CreateAsync<CompanyContactDto>(client, "/api/crm/company-contacts", new SaveCompanyContactRequest(company.Id, ayse.Id));
        var other = await CreateAsync<CompanyContactDto>(client, "/api/crm/company-contacts", new SaveCompanyContactRequest(company.Id, mehmet.Id));

        // İlişkiye özel e-posta ayrı bir ana nokta olabilir ve kişinin genel (eski) e-postasını değiştirmez.
        var work = await CreateAsync<PersonContactPointDto>(client, PersonPoints, new SavePersonContactPointRequest(
            ayse.Id, "ayse@kurum.com", "Email", link.Id, IsPrimary: true));
        Assert.Equal(link.Id, work.CompanyContactId);
        Assert.Equal("ortak@kutu.com", (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{ayse.Id}"))!.Email);

        // İlişki başka kişiye aitse reddedilir; aynı sahipte aynı değer tekrarlanamaz.
        var wrongLink = await client.PostAsJsonAsync(PersonPoints, new SavePersonContactPointRequest(ayse.Id, "x@y.com", "Email", other.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongLink.StatusCode);
        Assert.Contains("companyContactId", await wrongLink.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(PersonPoints, new SavePersonContactPointRequest(ayse.Id, "ORTAK@kutu.com", "Email"))).StatusCode);

        // Genel ana e-posta yeni API'den değişirse eski alan aynayı izler.
        var general = found.Items.Single(p => p.ContactId == ayse.Id);
        var changed = await client.PutAsJsonAsync($"{PersonPoints}/{general.Id}", new SavePersonContactPointRequest(
            ayse.Id, "yeni@kutu.com", "Email", null, IsPrimary: true, Version: general.Version));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("yeni@kutu.com", (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{ayse.Id}"))!.Email);
    }

    [Fact]
    public async Task Company_relations_block_self_links_duplicates_and_cycles_and_end_without_deleting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var a = await CreateCompanyAsync(client, "Holding A");
        var b = await CreateCompanyAsync(client, "İştirak B");
        var c = await CreateCompanyAsync(client, "Alt iştirak C");

        var self = await client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(a.Id, a.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);

        var ab = await CreateAsync<CompanyRelationDto>(client, Relations, new SaveCompanyRelationRequest(a.Id, b.Id));
        var bc = await CreateAsync<CompanyRelationDto>(client, Relations, new SaveCompanyRelationRequest(b.Id, c.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(a.Id, b.Id))).StatusCode);

        // Döngü: doğrudan (B→A) ve dolaylı (C→A).
        var direct = await client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(b.Id, a.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, direct.StatusCode);
        Assert.Contains("döngü", await direct.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(c.Id, a.Id))).StatusCode);

        // İki taraftan da listelenir.
        Assert.Equal(2, (await client.GetFromJsonAsync<CrmPage<CompanyRelationDto>>($"{Relations}?companyId={b.Id}"))!.TotalCount);

        // Bağ sonlanınca geçmiş kalır ve tersi bağ kurulabilir.
        var ended = await client.PutAsJsonAsync($"{Relations}/{bc.Id}", new SaveCompanyRelationRequest(
            b.Id, c.Id, EndDate: DateOnly.FromDateTime(DateTime.UtcNow), Version: bc.Version));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.False((await ended.Content.ReadFromJsonAsync<CompanyRelationDto>())!.IsActive);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<CompanyRelationDto>>($"{Relations}?companyId={c.Id}"))!.TotalCount);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(c.Id, a.Id))).StatusCode);

        // Ana ve bağlı firma yalnızca oluştururken verilir.
        var swapped = await client.PutAsJsonAsync($"{Relations}/{ab.Id}", new SaveCompanyRelationRequest(c.Id, b.Id, Version: ab.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, swapped.StatusCode);
    }

    [Fact]
    public async Task Opposite_relations_created_at_the_same_time_cannot_form_a_cycle()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        for (var round = 0; round < 3; round++)
        {
            var x = await CreateCompanyAsync(client, $"Yarış X{round}");
            var y = await CreateCompanyAsync(client, $"Yarış Y{round}");
            var responses = await Task.WhenAll(
                client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(x.Id, y.Id)),
                client.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(y.Id, x.Id)));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Contains((int)r.StatusCode, new[] { 409, 422 }));
            Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<CompanyRelationDto>>($"{Relations}?companyId={x.Id}&active=true"))!.TotalCount);
        }
    }

    [Fact]
    public async Task Old_company_and_contact_fields_stay_compatible_with_the_structured_records()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Eski istemci yalnızca tekil alanları gönderir; ana kayıtlar oluşur.
        var created = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(
            "Eski İstemci A.Ş.", Phone: "0212 555 11 22", Email: "Info@Eski.com", City: "İstanbul", Address: "Merkez Mah. 1. Sok. No:3"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<CompanyDto>())!;
        var points = (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?companyId={company.Id}"))!.Items;
        Assert.Equal(2, points.Count);
        var phone = points.Single(p => p.Channel == "Phone");
        var email = points.Single(p => p.Channel == "Email");
        Assert.True(phone.IsPrimary && phone.IsShared);
        Assert.Equal("+902125551122", phone.NormalizedValue);
        Assert.Equal("Info@Eski.com", email.Value);
        Assert.Equal("info@eski.com", email.NormalizedValue);
        var address = Assert.Single((await client.GetFromJsonAsync<CrmPage<AddressDto>>($"{Addresses}?companyId={company.Id}"))!.Items);
        Assert.Equal("Merkez Mah. 1. Sok. No:3", address.Line1);
        Assert.Equal("İstanbul", address.City);
        Assert.True(address.IsPrimary);

        // Ekranın tüm kaydı yeniden göndermesi hiçbir şeyi çoğaltmaz veya değiştirmez.
        var same = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}", Resend(company));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var again = (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?companyId={company.Id}"))!.Items;
        Assert.Equal(points.Select(p => (p.Id, p.Version)).Order(), again.Select(p => (p.Id, p.Version)).Order());

        // Eski alanı değiştirmek aynı ana noktayı günceller (ikinci kayıt açılmaz); boşaltmak noktayı pasifleştirir.
        company = (await same.Content.ReadFromJsonAsync<CompanyDto>())!;
        var edited = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}", Resend(company) with { Phone = "0216 444 00 00", Email = null });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var afterEdit = (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?companyId={company.Id}"))!.Items;
        Assert.Equal(2, afterEdit.Count);
        var updatedPhone = afterEdit.Single(p => p.Channel == "Phone");
        Assert.Equal((phone.Id, "0216 444 00 00", "+902164440000"), (updatedPhone.Id, updatedPhone.Value, updatedPhone.NormalizedValue));
        Assert.False(afterEdit.Single(p => p.Channel == "Email").IsActive);

        // Yeni uçtan ana noktayı değiştirmek eski alanı günceller: tek gerçek ana kayıttır.
        var newEmail = await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(company.Id, "yeni@eski.com", "Email", IsPrimary: true));
        var reloaded = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal("yeni@eski.com", reloaded.Email);
        Assert.Equal("0216 444 00 00", reloaded.Phone);
        var demoted = await client.PutAsJsonAsync($"{Points}/{newEmail.Id}", new SaveContactPointRequest(
            company.Id, "yeni@eski.com", "Email", IsPrimary: false, Version: newEmail.Version));
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!.Email);

        // Eski kişi alanları da aynı şekilde çalışır.
        var contact = await CreateContactAsync(client, "Eski", "Kişi", email: "eski@kisi.com", phone: "0533 111 22 33");
        var personPoints = (await client.GetFromJsonAsync<CrmPage<PersonContactPointDto>>($"{PersonPoints}?contactId={contact.Id}"))!.Items;
        Assert.Equal(2, personPoints.Count);
        var contactEdit = await client.PutAsJsonAsync($"/api/crm/contacts/{contact.Id}", new SaveContactRequest(
            "Eski", "Kişi", Email: "degisti@kisi.com", Phone: "0533 111 22 33", Version: contact.Version));
        Assert.Equal(HttpStatusCode.OK, contactEdit.StatusCode);
        var afterContact = (await client.GetFromJsonAsync<CrmPage<PersonContactPointDto>>($"{PersonPoints}?contactId={contact.Id}"))!.Items;
        Assert.Equal(2, afterContact.Count);
        Assert.Equal(personPoints.Single(p => p.Channel == "Email").Id, afterContact.Single(p => p.Channel == "Email").Id);
        Assert.Equal("degisti@kisi.com", afterContact.Single(p => p.Channel == "Email").Value);
    }

    [Fact]
    public async Task Directory_records_follow_the_company_scope_and_tenant_isolation()
    {
        var (tenant, admin) = await SetUpAsync();
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        var companiesOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite], Scope.All, companiesOnly);
        using var adminClient = await LoginAsync(admin);
        using var clientA = await LoginAsync(repA);
        using var clientB = await LoginAsync(repB);
        using var onlyCompanies = await LoginAsync(companiesOnly);

        var companyA = await CreateCompanyAsync(clientA, "A kapsamı");
        var companyA2 = await CreateCompanyAsync(clientA, "A ikinci");
        var companyB = await CreateCompanyAsync(clientB, "B kapsamı");
        var locationA = await CreateAsync<LocationDto>(clientA, Locations, new SaveLocationRequest(companyA.Id, "A deposu"));
        var addressA = await CreateAsync<AddressDto>(clientA, Addresses, new SaveAddressRequest(companyA.Id, "A cad."));
        var pointA = await CreateAsync<ContactPointDto>(clientA, Points, new SaveContactPointRequest(companyA.Id, "0212 111 11 11"));

        // B, A'nın kayıtlarını ne listede ne kimlikle görür; A'nın firmasına kayıt da ekleyemez.
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<LocationDto>>(Locations))!.TotalCount);
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<AddressDto>>(Addresses))!.TotalCount);
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<ContactPointDto>>(Points))!.TotalCount);
        foreach (var url in new[] { $"{Locations}/{locationA.Id}", $"{Addresses}/{addressA.Id}", $"{Points}/{pointA.Id}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"{Locations}/{locationA.Id}",
            new SaveLocationRequest(companyA.Id, "ele geçir", Version: locationA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"{Addresses}/{addressA.Id}?version={addressA.Version}")).StatusCode);
        foreach (var response in new[]
        {
            await clientB.PostAsJsonAsync(Locations, new SaveLocationRequest(companyA.Id, "sızma")),
            await clientB.PostAsJsonAsync(Addresses, new SaveAddressRequest(companyA.Id, "sızma")),
            await clientB.PostAsJsonAsync(Points, new SaveContactPointRequest(companyA.Id, "0212 999 99 99")),
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("companyId", await response.Content.ReadAsStringAsync());
        }

        // Firma ilişkisi iki ucu da kapsamdaysa görünür: A kendi iki firmasını bağlar, B'nin firmasına bağlayamaz.
        var own = await CreateAsync<CompanyRelationDto>(clientA, Relations, new SaveCompanyRelationRequest(companyA.Id, companyA2.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await clientA.PostAsJsonAsync(Relations, new SaveCompanyRelationRequest(companyA.Id, companyB.Id))).StatusCode);
        var cross = await CreateAsync<CompanyRelationDto>(adminClient, Relations, new SaveCompanyRelationRequest(companyA.Id, companyB.Id));
        Assert.Equal(own.Id, Assert.Single((await clientA.GetFromJsonAsync<CrmPage<CompanyRelationDto>>(Relations))!.Items).Id);
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<CompanyRelationDto>>(Relations))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"{Relations}/{cross.Id}")).StatusCode);

        // Kişi sahipli noktalar kişi izniyle ve kişinin görünürlüğüyle korunur.
        var contactA = await CreateContactAsync(clientA, "Ali", "Özel", email: "ali@a.com");
        var personPointA = (await clientA.GetFromJsonAsync<CrmPage<PersonContactPointDto>>(PersonPoints))!.Items.Single();
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<PersonContactPointDto>>(PersonPoints))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{PersonPoints}/{personPointA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await clientB.PostAsJsonAsync(PersonPoints, new SavePersonContactPointRequest(contactA.Id, "b@b.com", "Email"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await onlyCompanies.GetAsync(PersonPoints)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await onlyCompanies.GetAsync(Points)).StatusCode);

        // İki tenant: başka tenant'ın yöneticisi bu kayıtları göremez.
        var (_, otherAdmin) = await SetUpAsync();
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"{Locations}/{locationA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"{Relations}/{own.Id}")).StatusCode);
        Assert.Equal(0, (await otherClient.GetFromJsonAsync<CrmPage<ContactPointDto>>(Points))!.TotalCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await otherClient.PostAsJsonAsync(Locations, new SaveLocationRequest(companyA.Id, "tenant sızıntısı"))).StatusCode);
    }

    [Fact]
    public async Task Directory_changes_are_audited_versioned_and_company_deletion_is_guarded()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Silinecek A.Ş.");
        var location = await CreateAsync<LocationDto>(client, Locations, new SaveLocationRequest(company.Id, "Depo", "D1"));
        var address = await CreateAsync<AddressDto>(client, Addresses, new SaveAddressRequest(company.Id, "Cad. 1", IsPrimary: true, City: "Ankara"));
        var point = await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(company.Id, "0312 444 33 22", IsPrimary: true));

        var update = await client.PutAsJsonAsync($"{Locations}/{location.Id}",
            new SaveLocationRequest(company.Id, "Yeni depo", "D1", Version: location.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Locations}/{location.Id}",
            new SaveLocationRequest(company.Id, "Bayat", "D1", Version: location.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PutAsJsonAsync($"{Locations}/{location.Id}",
            new SaveLocationRequest(company.Id, "Sürümsüz", "D1"))).StatusCode);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = await db.AuditLog.Where(a => a.RecordId == location.Id).OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal([AuditAction.Created, AuditAction.Updated], audit.Select(a => a.Action));
            Assert.Contains("Yeni depo", audit[1].Changes);
            Assert.Equal(2, await db.OutboxMessages.CountAsync(e => e.RecordId == location.Id));
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.RecordId == address.Id));
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.RecordId == point.Id));
        }

        // Lokasyonu olan firma silinemez; lokasyon kalkınca firma silinir ve adres/iletişim noktaları da gider.
        var current = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/crm/companies/{company.Id}?version={current.Version}")).StatusCode);
        var currentLocation = (await client.GetFromJsonAsync<LocationDto>($"{Locations}/{location.Id}"))!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Locations}/{location.Id}?version={currentLocation.Version}")).StatusCode);
        current = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/crm/companies/{company.Id}?version={current.Version}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<AddressDto>>($"{Addresses}?companyId={company.Id}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"{Points}?companyId={company.Id}"))!.TotalCount);
    }


    [Fact]
    public async Task Company_and_person_search_and_filters_run_on_the_server_across_secondary_records()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // 205 firma: ekranın ilk 200 kaydı aşıldığında da arama sunucuda çalışır ve toplam doğrudur.
        for (var i = 0; i < 205; i++)
        {
            await CreateCompanyAsync(client, $"Toplu {i:D3} A.Ş.");
        }

        var target = await CreateCompanyAsync(client, "Hedef Ticaret A.Ş.");
        Assert.Equal(206, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=5"))!.TotalCount);
        Assert.Equal(target.Id, Assert.Single((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Hedef"))!.Items).Id);

        // İkincil iletişim ve adresler (ana kayıt değil) de aramada bulunur; farklı yazım biçimleri eşleşir.
        await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(target.Id, "0850 777 66 55", IsPrimary: true));
        await CreateAsync<ContactPointDto>(client, Points, new SaveContactPointRequest(target.Id, "muhasebe@hedef.com", "Email"));
        await CreateAsync<AddressDto>(client, Addresses, new SaveAddressRequest(target.Id, "Organize Sanayi", City: "Gebze", District: "Çayırova"));
        foreach (var term in new[] { "850-777-6655", "+90 850 777 66 55", "MUHASEBE@hedef.com", "cayirova" })
        {
            var found = await client.GetFromJsonAsync<CrmPage<CompanyDto>>($"/api/crm/companies?search={Uri.EscapeDataString(term)}");
            Assert.Equal(target.Id, Assert.Single(found!.Items).Id);
        }

        // Şehir filtresi adreslerden de çalışır; tür filtresi geçersiz değerde 422 verir.
        Assert.Equal(target.Id, Assert.Single((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?city=Gebze"))!.Items).Id);
        Assert.Equal(206, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?kind=Organization"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?kind=Individual"))!.TotalCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/crm/companies?kind=Alien")).StatusCode);

        // Kişi: ikinci e-posta ile arama ve ilişki rolüne göre süzme.
        var buyer = await CreateContactAsync(client, "Satın", "Alma");
        await CreateContactAsync(client, "Başka", "Kişi");
        var link = await CreateAsync<CompanyContactDto>(client, "/api/crm/company-contacts",
            new SaveCompanyContactRequest(target.Id, buyer.Id, Roles: [CrmRelationshipRoles.Purchasing]));
        await CreateAsync<PersonContactPointDto>(client, PersonPoints, new SavePersonContactPointRequest(buyer.Id, "alim@hedef.com", "Email", link.Id));
        Assert.Equal(buyer.Id, Assert.Single((await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?search=alim@hedef.com"))!.Items).Id);
        Assert.Equal(buyer.Id, Assert.Single((await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?role=Purchasing"))!.Items).Id);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?role=Finance"))!.TotalCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/crm/contacts?role=Admin")).StatusCode);
    }
    [Fact]
    public void Key_lists_match_between_server_and_shared_contracts()
    {
        Assert.Equal(CrmModule.LocationTypes.Order(), CrmLocationTypes.Labels.Keys.Order());
        Assert.Equal(CrmModule.AddressTypes.Order(), CrmAddressTypes.Labels.Keys.Order());
        Assert.Equal(CrmModule.ContactPointTypes.Order(), CrmContactPointTypes.Labels.Keys.Order());
        Assert.Equal(CrmModule.RelationTypes.Order(), CrmCompanyRelationTypes.Labels.Keys.Order());
        Assert.Equal(CrmModule.ContactChannels.Order(), new[] { CrmContactPointTypes.Phone, CrmContactPointTypes.Email }.Order());
    }

    private static SaveCompanyRequest Resend(CompanyDto c) => new(
        c.Name, c.TaxNumber, c.Phone, c.Email, c.Website, c.City, c.Notes, c.Version, c.Address, c.Industry, c.Status, c.Source, c.Tags,
        c.OwnerId, null, null, c.Kind, c.TaxOffice);

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return (tenant, admin);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name) =>
        await CreateAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest(name));

    private static async Task<ContactDto> CreateContactAsync(HttpClient client, string first, string last, string? email = null, string? phone = null) =>
        await CreateAsync<ContactDto>(client, "/api/crm/contacts", new SaveContactRequest(first, last, Email: email, Phone: phone));

    private static async Task<T> CreateAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{url}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
