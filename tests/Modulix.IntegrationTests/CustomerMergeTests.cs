using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0013: mükerrer önerisi (kesin ve olası eşleşme), mevcut ortak kaydı seçerek uzantı oluşturma ve güvenli birleştirme
/// (izin, kapsam, sürüm, tek işlem, iz, tekrar koruması, engeller).
/// </summary>
public class CustomerMergeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Companies = "/api/crm/companies";
    private const string Contacts = "/api/crm/contacts";
    private const string Links = "/api/crm/company-contacts";

    private static readonly string[] MergeAccess =
    [
        CompaniesRead, CompaniesWrite, CompaniesMerge, ContactsRead, ContactsWrite, ContactsMerge, OpportunitiesRead, OpportunitiesWrite,
        ActivitiesRead, ActivitiesWrite,
    ];

    [Fact]
    public async Task Company_merge_moves_related_records_merges_twins_and_leaves_a_trace()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var target = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Acme Lojistik A.Ş.", TaxNumber: "1111111111", Phone: "0216 555 00 11"));
        var source = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("ACME Lojistik Anonim Şirketi", Phone: "0212 111 22 33", City: "Gebze"));
        var ali = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Ali", "Veli"));
        var zeynep = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Zeynep", "Kaya"));

        // Ali iki firmada da aktif (ikiz ilişki); Zeynep yalnızca kaynakta ve oradaki ana irtibat.
        var aliTarget = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(target.Id, ali.Id, Roles: ["Purchasing"]));
        var aliSource = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(source.Id, ali.Id, Roles: ["Finance"], IsDecisionMaker: true));
        var zeynepSource = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(source.Id, zeynep.Id, Roles: ["Technical"], IsPrimary: true));
        await CreateAsync<PersonContactPointDto>(client, "/api/crm/person-contact-points",
            new SavePersonContactPointRequest(ali.Id, "ali.fin@acme.test", "Email", aliSource.Id));
        var depot = await CreateAsync<LocationDto>(client, "/api/crm/locations", new SaveLocationRequest(source.Id, "Gebze deposu", "GEB"));
        await CreateAsync<AddressDto>(client, "/api/crm/addresses", new SaveAddressRequest(source.Id, "Organize Sanayi 1", City: "Gebze"));
        await CreateAsync<AddressDto>(client, "/api/crm/addresses", new SaveAddressRequest(target.Id, "Merkez cad. 5", IsPrimary: true, City: "İstanbul"));
        var child = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Alt Firma Ltd."));
        await CreateAsync<CompanyRelationDto>(client, "/api/crm/company-relations", new SaveCompanyRelationRequest(source.Id, child.Id));
        await CreateAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest("Yıllık anlaşma", source.Id));
        await CreateAsync<ActivityDto>(client, "/api/crm/activities", new SaveActivityRequest("Ara", CompanyId: source.Id));

        // Önizleme yazmaz: kaynak yerinde kalır.
        (source, target) = (await GetAsync<CompanyDto>(client, Companies, source.Id), await GetAsync<CompanyDto>(client, Companies, target.Id));
        var request = new MergeRecordsRequest(source.Id, source.Version, target.Version, new() { ["phone"] = "source", ["city"] = "source" });
        var preview = await PostAsync<MergePreviewDto>(client, $"{Companies}/{target.Id}/merge/preview", request);
        Assert.Empty(preview.Blockers);
        // Hedefin değeri doluysa hedef önerilir; boşsa kaynak önerilir (kaynağın vergi no'su boş, hedefinki dolu).
        Assert.Contains(preview.Fields, f => f.Field == "phone" && f.Suggested == "target");
        Assert.Contains(preview.Impacts, i => i.Label.Contains("hedefteki ilişkiyle birleşir") && i.Count == 1);
        Assert.Contains(preview.Impacts, i => i.Label == "Kişi ilişkisi taşınır" && i.Count == 1);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Companies}/{source.Id}")).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"/api/crm/locations?companyId={source.Id}"))!.TotalCount);

        var merged = await PostAsync<MergeResultDto>(client, $"{Companies}/{target.Id}/merge", request);
        Assert.Equal(target.Id, merged.TargetId);
        Assert.True(merged.TargetVersion > target.Version);

        // Kaynak kalktı; eski kimlik hedefe çözülür.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Companies}/{source.Id}")).StatusCode);
        var trace = (await client.GetFromJsonAsync<MergedRecordDto>($"{Companies}/merged/{source.Id}"))!;
        Assert.Equal(target.Id, trace.TargetId);

        // Seçilen alanlar kaynaktan geldi, seçilmeyen hedefte kaldı.
        var result = await GetAsync<CompanyDto>(client, Companies, target.Id);
        Assert.Equal("0212 111 22 33", result.Phone);
        Assert.Equal("Gebze", result.City);
        Assert.Equal("Acme Lojistik A.Ş.", result.Name);
        Assert.Equal("1111111111", result.TaxNumber);

        // İki firmanın da ana telefonu vardı: seçilen kaynağınki ana oldu, hedefinki ikincil kaldı (tek ana nokta).
        var phones = (await client.GetFromJsonAsync<CrmPage<ContactPointDto>>($"/api/crm/contact-points?companyId={target.Id}&channel=Phone"))!.Items;
        Assert.Equal(2, phones.Count);
        Assert.Equal("0212 111 22 33", Assert.Single(phones, p => p.IsPrimary).Value);

        // İlişkiler: ikiz birleşti (roller birleşik), Zeynep taşındı ve ana irtibat oldu; kişilerin firması hedef.
        var links = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?companyId={target.Id}"))!.Items;
        Assert.Equal(2, links.Count);
        var mergedAli = Assert.Single(links, l => l.ContactId == ali.Id);
        Assert.Equal(aliTarget.Id, mergedAli.Id);
        Assert.Contains("Purchasing", mergedAli.Roles);
        Assert.Contains("Finance", mergedAli.Roles);
        Assert.True(mergedAli.IsDecisionMaker);
        Assert.True(Assert.Single(links, l => l.ContactId == zeynep.Id).IsPrimary);
        Assert.Equal(target.Id, (await GetAsync<ContactDto>(client, Contacts, zeynep.Id)).CompanyId);
        Assert.Equal(target.Id, (await GetAsync<ContactDto>(client, Contacts, ali.Id)).CompanyId);

        // İlişkiye özel iletişim noktası ikiz ilişkiye geçti.
        var points = (await client.GetFromJsonAsync<CrmPage<PersonContactPointDto>>($"/api/crm/person-contact-points?contactId={ali.Id}"))!.Items;
        Assert.Equal(aliTarget.Id, Assert.Single(points, p => p.Value == "ali.fin@acme.test").CompanyContactId);

        // Lokasyon, adres (ana adres hedefinki kaldı), firma ilişkisi, fırsat ve aktivite hedefte.
        Assert.Equal(target.Id, (await client.GetFromJsonAsync<CrmPage<LocationDto>>("/api/crm/locations?companyId=" + target.Id))!.Items.Single().CompanyId);
        var addresses = (await client.GetFromJsonAsync<CrmPage<AddressDto>>($"/api/crm/addresses?companyId={target.Id}"))!.Items;
        // Kaynakta şehir alanının aynası olan ana adres ve bir ek adres, hedefte "Merkez cad. 5" vardı. "city" kaynaktan seçildiği için
        // ana adres kaynağın içeriğini aldı; hedefin eski ana adresi ikincil olarak kaldı (hiçbir adres kaybolmadı, tek ana adres var).
        Assert.Equal(3, addresses.Count);
        Assert.Equal("Gebze", Assert.Single(addresses, a => a.IsPrimary).City);
        Assert.Contains(addresses, a => a.Line1 == "Merkez cad. 5" && !a.IsPrimary);
        Assert.Contains(addresses, a => a.Line1 == "Organize Sanayi 1");
        Assert.Equal(target.Id, (await client.GetFromJsonAsync<CrmPage<CompanyRelationDto>>("/api/crm/company-relations?parentCompanyId=" + target.Id))!.Items.Single().ParentCompanyId);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities?companyId=" + target.Id))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<ActivityDto>>("/api/crm/activities?companyId=" + target.Id))!.TotalCount);
        _ = depot;

        // Aynı istek tekrarlanırsa sessizce başarılı olmaz; kaynağın zaten birleştirildiği söylenir.
        var replay = await client.PostAsJsonAsync($"{Companies}/{target.Id}/merge", request);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("zaten birleştirilmiş", await replay.Content.ReadAsStringAsync());

        // Hedefin geçmişinde birleştirme görünür.
        var history = await client.GetStringAsync($"/api/records/crm.company/{target.Id}/history");
        Assert.Contains(source.Id.ToString(), history);
    }

    [Fact]
    public async Task Stale_versions_and_concurrent_merges_leave_data_consistent()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var source = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Yarış Kaynak A.Ş."));
        var one = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Yarış Hedef Bir A.Ş."));
        var two = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Yarış Hedef İki A.Ş."));
        await CreateAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", source.Id));

        // Eski sürümle birleştirme 409; hiçbir şey taşınmaz.
        var stale = await client.PostAsJsonAsync($"{Companies}/{one.Id}/merge", new MergeRecordsRequest(source.Id, source.Version - 1, one.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Companies}/{source.Id}")).StatusCode);

        // Aynı kaynağı iki hedefe birlikte birleştiren iki istekten yalnızca biri başarılı olur.
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"{Companies}/{one.Id}/merge", new MergeRecordsRequest(source.Id, source.Version, one.Version)),
            client.PostAsJsonAsync($"{Companies}/{two.Id}/merge", new MergeRecordsRequest(source.Id, source.Version, two.Version)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains((int)r.StatusCode, new[] { 404, 409, 422 }));

        // Fırsat tam olarak bir hedefe taşındı (kaybolmadı, çoğalmadı).
        var counts = new[] { one.Id, two.Id }.Select(async id =>
            (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities?companyId=" + id))!.TotalCount);
        Assert.Equal(1, (await Task.WhenAll(counts)).Sum());
    }

    [Fact]
    public async Task Merge_needs_its_own_permission_and_both_records_in_the_callers_scope_and_tenant()
    {
        var (tenant, admin) = await SetUpAsync();
        var rep = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, MergeAccess, Scope.Own, rep);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite], Scope.All, writer);
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        using var writerClient = await LoginAsync(writer);
        var mineA = await CreateAsync<CompanyDto>(repClient, Companies, new SaveCompanyRequest("Benim A"));
        var mineB = await CreateAsync<CompanyDto>(repClient, Companies, new SaveCompanyRequest("Benim B"));
        var others = await CreateAsync<CompanyDto>(adminClient, Companies, new SaveCompanyRequest("Yöneticinin firması"));

        // Birleştirme izni olmadan 403 (yazma izni yetmez).
        var denied = await writerClient.PostAsJsonAsync($"{Companies}/{mineA.Id}/merge", new MergeRecordsRequest(mineB.Id, mineB.Version, mineA.Version));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writerClient.PostAsJsonAsync($"{Companies}/{mineA.Id}/merge/preview", new MergeRecordsRequest(mineB.Id, 1, 1))).StatusCode);

        // Kapsam dışı kayıt kaynak da hedef de olamaz: var olmayan kayıtla aynı cevap (404).
        Assert.Equal(HttpStatusCode.NotFound,
            (await repClient.PostAsJsonAsync($"{Companies}/{mineA.Id}/merge", new MergeRecordsRequest(others.Id, others.Version, mineA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await repClient.PostAsJsonAsync($"{Companies}/{others.Id}/merge/preview", new MergeRecordsRequest(mineA.Id, mineA.Version, others.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"{Companies}/{others.Id}")).StatusCode);

        // Kapsamındaki iki kayıt birleşir; eski kimliğin izini kapsam dışındaki göremez.
        var ok = await repClient.PostAsJsonAsync($"{Companies}/{mineA.Id}/merge", new MergeRecordsRequest(mineB.Id, mineB.Version, mineA.Version));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await repClient.GetAsync($"{Companies}/merged/{mineB.Id}")).StatusCode);

        // Başka tenant'ın kayıtları görünmez.
        var (_, foreignAdmin) = await SetUpAsync();
        using var foreignClient = await LoginAsync(foreignAdmin);
        var foreignTarget = await CreateAsync<CompanyDto>(foreignClient, Companies, new SaveCompanyRequest("Yabancı hedef"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await foreignClient.PostAsJsonAsync($"{Companies}/{foreignTarget.Id}/merge", new MergeRecordsRequest(others.Id, others.Version, foreignTarget.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync($"{Companies}/merged/{mineB.Id}")).StatusCode);
    }

    [Fact]
    public async Task Merge_refuses_unresolved_conflicts_changes_nothing_and_rejects_bad_requests()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var target = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Çakışma Hedef A.Ş."));
        var source = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Çakışma Kaynak A.Ş."));

        // Kendisiyle ve bilinmeyen alan seçimiyle birleştirme reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"{Companies}/{target.Id}/merge", new MergeRecordsRequest(target.Id, target.Version, target.Version))).StatusCode);
        var unknown = await client.PostAsJsonAsync($"{Companies}/{target.Id}/merge",
            new MergeRecordsRequest(source.Id, source.Version, target.Version, new() { ["yok"] = "source" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"{Companies}/{target.Id}/merge",
                new MergeRecordsRequest(source.Id, source.Version, target.Version, new() { ["phone"] = "ikisi" }))).StatusCode);

        // Aynı lokasyon kodu iki firmada da varsa birleştirme engellenir ve hiçbir şey taşınmaz.
        await CreateAsync<LocationDto>(client, "/api/crm/locations", new SaveLocationRequest(target.Id, "Depo", "D1"));
        await CreateAsync<LocationDto>(client, "/api/crm/locations", new SaveLocationRequest(source.Id, "Depo (eski)", "d1"));
        await CreateAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest("Taşınmamalı", source.Id));
        (source, target) = (await GetAsync<CompanyDto>(client, Companies, source.Id), await GetAsync<CompanyDto>(client, Companies, target.Id));
        var request = new MergeRecordsRequest(source.Id, source.Version, target.Version);
        var preview = await PostAsync<MergePreviewDto>(client, $"{Companies}/{target.Id}/merge/preview", request);
        Assert.Contains(preview.Blockers, b => b.Contains("Lokasyon kodu"));
        var refused = await client.PostAsJsonAsync($"{Companies}/{target.Id}/merge", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Companies}/{source.Id}")).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities?companyId=" + source.Id))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Companies}/merged/{source.Id}")).StatusCode);

        // Tüzel kişi ile gerçek kişi firması birleştirilmez.
        var person = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Şahıs Firması", TaxNumber: "12345678901", Kind: "Individual"));
        var kinds = await PostAsync<MergePreviewDto>(client, $"{Companies}/{target.Id}/merge/preview", new MergeRecordsRequest(person.Id, person.Version, target.Version));
        Assert.Contains(kinds.Blockers, b => b.Contains("gerçek kişi"));

        // Firma ilişkilerinde döngü oluşturacak birleştirme engellenir: T→X ve X→S iken S, T'ye katılamaz.
        var t = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Döngü T"));
        var x = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Döngü X"));
        var s = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Döngü S"));
        await CreateAsync<CompanyRelationDto>(client, "/api/crm/company-relations", new SaveCompanyRelationRequest(t.Id, x.Id));
        await CreateAsync<CompanyRelationDto>(client, "/api/crm/company-relations", new SaveCompanyRelationRequest(x.Id, s.Id));
        (s, t) = (await GetAsync<CompanyDto>(client, Companies, s.Id), await GetAsync<CompanyDto>(client, Companies, t.Id));
        var cycle = await PostAsync<MergePreviewDto>(client, $"{Companies}/{t.Id}/merge/preview", new MergeRecordsRequest(s.Id, s.Version, t.Version));
        Assert.Contains(cycle.Blockers, b => b.Contains("döngü"));

        // Hedefte vergi no yokken kaynaktaki seçilirse hedef alır (kaynak aynı işlemde silinir; benzersizlik bozulmaz).
        var withTax = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Vergili Kaynak", TaxNumber: "2222222222"));
        var without = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Vergisiz Hedef"));
        var taxed = await client.PostAsJsonAsync($"{Companies}/{without.Id}/merge",
            new MergeRecordsRequest(withTax.Id, withTax.Version, without.Version, new() { ["taxNumber"] = "source" }));
        Assert.Equal(HttpStatusCode.OK, taxed.StatusCode);
        Assert.Equal("2222222222", (await GetAsync<CompanyDto>(client, Companies, without.Id)).TaxNumber);
    }

    [Fact]
    public async Task Contact_merge_unifies_relationships_points_and_references()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Kişi Birleştirme A.Ş."));
        var other = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("İkinci Firma A.Ş."));
        var target = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Mehmet", "Yılmaz", Title: "Müdür"));
        var source = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Mehmet", "Yilmaz", Notes: "Eski kayıt notu"));
        var targetLink = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(company.Id, target.Id, Roles: ["Purchasing"]));
        var sourceLink = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(company.Id, source.Id, Roles: ["Finance"], IsPrimary: true));
        var sourceOther = await CreateAsync<CompanyContactDto>(client, Links, new SaveCompanyContactRequest(other.Id, source.Id, Roles: ["Technical"]));
        await CreateAsync<PersonContactPointDto>(client, "/api/crm/person-contact-points", new SavePersonContactPointRequest(source.Id, "mehmet@yilmaz.test", "Email"));
        await CreateAsync<PersonContactPointDto>(client, "/api/crm/person-contact-points", new SavePersonContactPointRequest(target.Id, "mehmet@yilmaz.test", "Email"));
        await CreateAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest("Kişili fırsat", company.Id, source.Id));
        await CreateAsync<ActivityDto>(client, "/api/crm/activities", new SaveActivityRequest("Görüşme", ContactId: source.Id));
        await CreateAsync<LocationDto>(client, "/api/crm/locations", new SaveLocationRequest(company.Id, "Şube", ContactId: source.Id));

        (source, target) = (await GetAsync<ContactDto>(client, Contacts, source.Id), await GetAsync<ContactDto>(client, Contacts, target.Id));
        var request = new MergeRecordsRequest(source.Id, source.Version, target.Version);
        var preview = await PostAsync<MergePreviewDto>(client, $"{Contacts}/{target.Id}/merge/preview", request);
        Assert.Empty(preview.Blockers);
        Assert.Equal("Mehmet Yilmaz", preview.SourceName);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Contacts}/{source.Id}")).StatusCode);

        await PostAsync<MergeResultDto>(client, $"{Contacts}/{target.Id}/merge", request);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Contacts}/{source.Id}")).StatusCode);
        Assert.Equal(target.Id, (await client.GetFromJsonAsync<MergedRecordDto>($"{Contacts}/merged/{source.Id}"))!.TargetId);

        // Notlar kaybolmaz; aynı firmadaki iki ilişki birleşti, diğer firma ilişkisi taşındı.
        var merged = await GetAsync<ContactDto>(client, Contacts, target.Id);
        Assert.Equal("Eski kayıt notu", merged.Notes);
        Assert.Equal("Müdür", merged.Title);
        var links = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={target.Id}"))!.Items;
        Assert.Equal(2, links.Count);
        var same = Assert.Single(links, l => l.CompanyId == company.Id);
        Assert.Equal(targetLink.Id, same.Id);
        Assert.Contains("Finance", same.Roles);
        Assert.True(same.IsPrimary);
        Assert.Single(links, l => l.Id == sourceOther.Id);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={source.Id}"))!.Items);
        _ = sourceLink;

        // Aynı e-posta tek nokta kalır; fırsat, aktivite ve lokasyon yetkilisi başvuruları hedefte.
        var points = (await client.GetFromJsonAsync<CrmPage<PersonContactPointDto>>($"/api/crm/person-contact-points?contactId={target.Id}"))!.Items;
        Assert.Single(points, p => p.Value == "mehmet@yilmaz.test");
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>("/api/crm/opportunities?contactId=" + target.Id))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<ActivityDto>>("/api/crm/activities?contactId=" + target.Id))!.TotalCount);
        Assert.Equal(target.Id, (await client.GetFromJsonAsync<CrmPage<LocationDto>>($"/api/crm/locations?companyId={company.Id}"))!.Items.Single().ContactId);

        // Ortak kişi kaydı da kalktı.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/records/core.person/{source.PersonId}")).StatusCode);
    }

    [Fact]
    public async Task Company_merge_repoints_shared_person_references_to_the_target_party()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var target = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Taraf Hedef A.Ş."));
        var source = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Taraf Kaynak A.Ş."));

        // Başka bir modülün (Core kişi kaydının) kaynak tarafa başvurusu: birleştirmeden sonra hedef tarafa bakar.
        var person = await client.PostAsJsonAsync("/api/records/core.person",
            new RecordWriteRequest(new JsonObject { ["firstName"] = "Bağımsız", ["lastName"] = "Kişi", ["partyId"] = source.PartyId.ToString() }));
        Assert.Equal(HttpStatusCode.Created, person.StatusCode);
        var personId = (await person.Content.ReadFromJsonAsync<RecordResponse>())!.Id;

        await PostAsync<MergeResultDto>(client, $"{Companies}/{target.Id}/merge", new MergeRecordsRequest(source.Id, source.Version, target.Version));

        var moved = (await client.GetFromJsonAsync<RecordResponse>($"/api/records/core.person/{personId}"))!;
        Assert.Equal(target.PartyId.ToString(), moved.Values["partyId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/records/core.party/{source.PartyId}")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_suggestions_separate_exact_from_similar_and_ignore_shared_values()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var acme = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Acme Lojistik A.Ş.", TaxNumber: "3333333333"));
        await CreateAsync<ContactPointDto>(client, "/api/crm/contact-points", new SaveContactPointRequest(acme.Id, "satis@acme.test", "Email"));
        await CreateAsync<ContactPointDto>(client, "/api/crm/contact-points", new SaveContactPointRequest(acme.Id, "0850 000 11 22", "Phone", IsShared: true));
        var other = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Tamamen Başka Firma"));
        await CreateAsync<ContactPointDto>(client, "/api/crm/contact-points", new SaveContactPointRequest(other.Id, "0850 000 11 22", "Phone", IsShared: true));

        // Vergi numarası kesin eşleşmedir; ad varyantı ve paylaşılmayan e-posta olası eşleşmedir.
        var exact = (await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Companies}/duplicates?taxNumber=3333333333"))!;
        var hit = Assert.Single(exact);
        Assert.Equal(CrmMatchKinds.Exact, hit.Match);
        Assert.Equal(acme.Id, hit.Id);
        var similar = (await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Companies}/duplicates?name=ACME%20Lojistik%20Anonim%20%C5%9Eirketi&email=SATIS@acme.test"))!;
        var variant = Assert.Single(similar);
        Assert.Equal(CrmMatchKinds.Similar, variant.Match);
        Assert.Contains("Ad benzer", variant.Reasons);
        Assert.Contains("Aynı e-posta", variant.Reasons);

        // Paylaşılan santral eşleşme kanıtı değildir; düzenlenen kaydın kendisi önerilmez.
        Assert.Empty((await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Companies}/duplicates?phone=0850%20000%2011%2022"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Companies}/duplicates?taxNumber=3333333333&excludeId={acme.Id}"))!);

        // Kişide yalnızca olası eşleşme vardır; aynı e-posta farklı kişide paylaşılansa eşleşme sayılmaz.
        var ayse = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Ayşe", "Demir"));
        var can = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Can", "Öztürk"));
        await CreateAsync<PersonContactPointDto>(client, "/api/crm/person-contact-points", new SavePersonContactPointRequest(ayse.Id, "ayse@demir.test", "Email"));
        await CreateAsync<PersonContactPointDto>(client, "/api/crm/person-contact-points", new SavePersonContactPointRequest(can.Id, "info@ortak.test", "Email", IsShared: true));
        var byName = (await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Contacts}/duplicates?firstName=Ayse&lastName=Demir&email=ayse@demir.test"))!;
        var person = Assert.Single(byName);
        Assert.Equal(CrmMatchKinds.Similar, person.Match);
        Assert.Contains("Aynı ad ve soyad", person.Reasons);
        Assert.Empty((await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Contacts}/duplicates?email=info@ortak.test"))!);
    }

    [Fact]
    public async Task An_existing_shared_record_can_be_selected_to_create_the_crm_extension()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Başka bir modülde (ortak kayıt olarak) açılmış firma: CRM uzantısı yok; öneri onu kimliğiyle verir.
        var party = await client.PostAsJsonAsync("/api/records/core.party",
            new RecordWriteRequest(new JsonObject { ["name"] = "Lojistik Müşterisi A.Ş.", ["taxNumber"] = "4444444444" }));
        Assert.Equal(HttpStatusCode.Created, party.StatusCode);
        var partyId = (await party.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
        var suggestion = Assert.Single((await client.GetFromJsonAsync<List<DuplicateCandidateDto>>($"{Companies}/duplicates?taxNumber=4444444444"))!);
        Assert.Null(suggestion.Id);
        Assert.Equal(partyId, suggestion.SharedId);
        Assert.Equal(CrmMatchKinds.Exact, suggestion.Match);

        // Seçilen kayıtla CRM uzantısı açılır (yeni taraf açılmaz); ikinci deneme reddedilir.
        var company = await CreateAsync<CompanyDto>(client, Companies, new SaveCompanyRequest("Lojistik Müşterisi A.Ş.", PartyId: partyId));
        Assert.Equal(partyId, company.PartyId);
        Assert.Equal("4444444444", company.TaxNumber);
        var again = await client.PostAsJsonAsync(Companies, new SaveCompanyRequest("Yine", PartyId: partyId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Contains("partyId", await again.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(Companies, new SaveCompanyRequest("Yok", PartyId: Guid.NewGuid()))).StatusCode);

        // Kişide aynı akış: mevcut ortak kişiyle CRM kişisi açılır.
        var person = await client.PostAsJsonAsync("/api/records/core.person",
            new RecordWriteRequest(new JsonObject { ["firstName"] = "Ortak", ["lastName"] = "Kişi" }));
        var personId = (await person.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
        var contact = await CreateAsync<ContactDto>(client, Contacts, new SaveContactRequest("Ortak", "Kişi", PersonId: personId));
        Assert.Equal(personId, contact.PersonId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(Contacts, new SaveContactRequest("Ortak", "Kişi", PersonId: personId))).StatusCode);
    }

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

    private static async Task<T> GetAsync<T>(HttpClient client, string url, Guid id) =>
        (await client.GetFromJsonAsync<T>($"{url}/{id}"))!;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> CreateAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
