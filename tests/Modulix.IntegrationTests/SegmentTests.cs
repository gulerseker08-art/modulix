using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using Modulix.Shared.Records;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 2: segment motoru ve yönetimi. Dört test türü: mutlu yol (AND/OR, NULL, ilişki, yayım, üyelik), hata/doğrulama,
/// tenant izolasyonu ve yetki (alan yetkisi, veri kapsamı, eylem izinleri).
/// </summary>
public class SegmentTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
    ];

    private static readonly string[] AllMarketing = [SegmentsRead, SegmentsWrite, SegmentsPublish, RecipientsRead];

    // ---- Arayüz sözleşmesi ----

    [Fact]
    public void Shared_access_keys_match_the_marketing_module()
    {
        // Arayüz menüsü ve ekranlar bu anahtarlara bakar; modüldeki bir değişiklik arayüzü sessizce bozmasın.
        Assert.Equal(MarketingModule.ModuleKey, MarketingAccess.ModuleKey);
        var modulePermissions = ((Modulix.Core.Contracts.Modules.IModule)new MarketingModule()).Permissions.Select(p => p.Key).Order().ToList();
        Assert.Equal(
            modulePermissions,
            typeof(MarketingAccess).GetFields().Where(f => f.Name != nameof(MarketingAccess.ModuleKey)).Select(f => (string)f.GetValue(null)!).Order());
    }

    // ---- Mutlu yol ----

    [Fact]
    public async Task Conditions_combine_with_and_or_null_semantics_and_relations_without_duplicates()
    {
        var (client, _) = await ManagerAsync();
        var a = await CompanyAsync(client, "Anka Tekstil", "Bursa", "Tekstil");
        await CompanyAsync(client, "Bora Tekstil", "İzmir", "Tekstil");
        await CompanyAsync(client, "Cem Gıda", "Bursa", "Gıda");
        await CompanyAsync(client, "Deniz Tekstil", null, "Tekstil");
        // Aynı firmanın iki kişisi: "kişisi var" firmayı iki kez saymaz (sonuç kimlik kümesidir).
        await ContactAsync(client, a.Id, "Ayşe", "Demir");
        await ContactAsync(client, a.Id, "Can", "Kaya");

        Assert.Equal(1, (await CountAsync(client, And(Leaf("crm.company.industry", "eq", "Tekstil"), Leaf("crm.company.city", "eq", "Bursa")))));
        Assert.Equal(3, await CountAsync(client, Or(Leaf("crm.company.city", "eq", "Bursa"), Leaf("crm.company.city", "eq", "İzmir"))));
        Assert.Equal(2, await CountAsync(client, Leaf("crm.company.city", "ne", "Bursa")));
        Assert.Equal(1, await CountAsync(client, Leaf("crm.company.city", "empty")));
        Assert.Equal(3, await CountAsync(client, Leaf("crm.company.city", "notempty")));
        Assert.Equal(2, await CountAsync(client, And(Leaf("crm.company.industry", "eq", "Tekstil"), Or(Leaf("crm.company.city", "eq", "Bursa"), Leaf("crm.company.city", "empty")))));
        Assert.Equal(3, await CountAsync(client, Leaf("crm.company.name", "contains", "Tekstil")));
        Assert.Equal(1, await CountAsync(client, Leaf("crm.company.contacts", "exists")));
        Assert.Equal(3, await CountAsync(client, Leaf("crm.company.contacts", "notexists")));
        Assert.Equal(4, await CountAsync(client, Leaf("crm.company.createdAt", "lastdays", "1")));
        Assert.Equal(0, await CountAsync(client, Leaf("crm.company.createdAt", "before", "2020-01-01")));

        var preview = await PreviewAsync(client, "Company", Leaf("crm.company.industry", "eq", "Gıda"));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = (await preview.Content.ReadFromJsonAsync<SegmentEvaluationDto>())!;
        Assert.Equal("Cem Gıda", Assert.Single(body.Rows).Label);
        Assert.False(body.RowsHidden);
    }

    [Fact]
    public async Task Publishing_freezes_a_version_and_later_edits_never_rewrite_history()
    {
        var (client, _) = await ManagerAsync();
        await CompanyAsync(client, "Anka", "Bursa", "Tekstil");
        await CompanyAsync(client, "Bora", "İzmir", "Tekstil");
        var segment = await CreateSegmentAsync(client, "tekstil-bursa", Leaf("crm.company.city", "eq", "Bursa"));
        Assert.Equal(("Draft", 0), (segment.Status, segment.PublishedVersion));

        var published = await PublishAsync(client, segment.Id);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var v1 = (await published.Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal(("Active", 1, 1), (v1.Status, v1.PublishedVersion, v1.LastMemberCount));
        Assert.NotNull(v1.LastComputedAt);

        // Aynı taslağı yeniden yayımlamak yeni sürüm açmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(client, segment.Id)).StatusCode);

        // Taslak değişir; üyelik hâlâ yayımlanmış sürümden hesaplanır.
        var edited = await client.PutAsJsonAsync($"/api/marketing/segments/{segment.Id}", Save(v1) with { Draft = Leaf("crm.company.city", "eq", "İzmir").ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var members = (await client.GetFromJsonAsync<SegmentEvaluationDto>($"/api/marketing/segments/{segment.Id}/members"))!;
        Assert.Equal("Anka", Assert.Single(members.Rows).Label);

        var v2 = (await (await PublishAsync(client, segment.Id)).Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal(2, v2.PublishedVersion);
        Assert.Equal("Bora", Assert.Single((await client.GetFromJsonAsync<SegmentEvaluationDto>($"/api/marketing/segments/{segment.Id}/members"))!.Rows).Label);

        var versions = (await client.GetFromJsonAsync<PagedResult<SegmentVersionDto>>($"/api/marketing/segment-versions?segmentId={segment.Id}"))!.Items;
        Assert.Equal([2, 1], versions.Select(v => v.VersionNo));
        Assert.Contains("Bursa", versions[1].Definition);
        Assert.Contains("İzmir", versions[0].Definition);

        // Yayımlanmış sürüm değiştirilemez, silinemez, elle oluşturulamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/marketing/segment-versions/{versions[1].Id}", new SaveSegmentVersionRequest(versions[1].Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/marketing/segment-versions/{versions[1].Id}?version={versions[1].Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/segment-versions", new SaveSegmentVersionRequest())).StatusCode);

        // Yenileme: yeni uygun firma gelince son hesaplama özeti güncellenir; segment kaydı değişmez ama sayı artar.
        await CompanyAsync(client, "Ece", "İzmir", "Gıda");
        var refreshed = (await (await client.PostAsync($"/api/marketing/segments/{segment.Id}/refresh", null)).Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal(2, refreshed.LastMemberCount);
        Assert.Equal(2, refreshed.PublishedVersion);
    }

    [Fact]
    public async Task Static_members_and_dynamic_exceptions_follow_include_minus_exclude()
    {
        var (client, _) = await ManagerAsync();
        var anka = await CompanyAsync(client, "Anka", "Bursa", "Tekstil");
        var bora = await CompanyAsync(client, "Bora", "İzmir", "Tekstil");
        var cem = await CompanyAsync(client, "Cem", "Bursa", "Gıda");

        // Statik: üyeler elle seçilir; mükerrer ve hariç tutma reddedilir.
        var stat = await CreateSegmentAsync(client, "fuar-listesi", null, kind: "Static");
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(client, stat.Id, anka.PartyId!.Value, "Include")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(client, stat.Id, bora.PartyId!.Value, "Include")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AddMemberAsync(client, stat.Id, anka.PartyId!.Value, "Include")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(client, stat.Id, cem.PartyId!.Value, "Exclude")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, stat.Id)).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<SegmentEvaluationDto>($"/api/marketing/segments/{stat.Id}/members"))!.Count);

        // Dinamik: koşul ∪ dahil ∖ hariç. Hariç tutma dahil etmenin önüne geçer.
        var dynamic = await CreateSegmentAsync(client, "bursa", Leaf("crm.company.city", "eq", "Bursa"));
        await PublishAsync(client, dynamic.Id);
        Assert.Equal(2, (await client.GetFromJsonAsync<SegmentEvaluationDto>($"/api/marketing/segments/{dynamic.Id}/members"))!.Count);

        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(client, dynamic.Id, anka.PartyId!.Value, "Exclude", "Davet etmeyin")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(client, dynamic.Id, bora.PartyId!.Value, "Include")).StatusCode);
        var result = (await client.GetFromJsonAsync<SegmentEvaluationDto>($"/api/marketing/segments/{dynamic.Id}/members"))!;
        Assert.Equal(["Bora", "Cem"], result.Rows.Select(r => r.Label).Order());
        Assert.Equal(2, result.Count);
    }

    // ---- Hata ve doğrulama ----

    [Fact]
    public async Task Invalid_conditions_and_segment_rules_are_rejected_with_field_errors()
    {
        var (client, _) = await ManagerAsync();

        var unknownField = await PreviewAsync(client, "Company", Leaf("crm.company.yok", "eq", "x"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownField.StatusCode);
        Assert.Contains("condition.field", Errors(await unknownField.Content.ReadFromJsonAsync<JsonObject>()).Select(e => e));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(client, "Company", Leaf("crm.company.city", "between", "a", "b"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(client, "Company", Leaf("crm.company.createdAt", "after", "dün"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(client, "Company", Leaf("crm.lead.city", "eq", "Bursa"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(client, "Gezegen", Leaf("crm.company.city", "eq", "Bursa"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(client, "Company", new JsonObject { ["sql"] = "1=1" })).StatusCode);

        // Taslak geçersizse kaydedilmez; yayım için taslak şarttır; statikte koşul olmaz.
        var invalidDraft = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "kotu", Name: "Kötü", Target: "Company", Kind: "Dynamic", Draft: "{ değil"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidDraft.StatusCode);
        var empty = await CreateSegmentAsync(client, "taslaksiz", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PublishAsync(client, empty.Id)).StatusCode);
        var staticWithDraft = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "st", Name: "St", Kind: "Static", Draft: Leaf("crm.company.city", "eq", "x").ToJsonString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, staticWithDraft.StatusCode);

        // Yayım, geçersiz alanlı taslağı sürüm olarak yazmaz.
        var bad = await CreateSegmentAsync(client, "gecersiz", Leaf("crm.company.yok", "eq", "x"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PublishAsync(client, bad.Id)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<SegmentVersionDto>>($"/api/marketing/segment-versions?segmentId={bad.Id}"))!.Items);
        Assert.Equal("Draft", (await client.GetFromJsonAsync<SegmentDto>($"/api/marketing/segments/{bad.Id}"))!.Status);

        // Kod biçimi, tekillik; kod/hedef/tür sonradan değişmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "Boşluk Var", Name: "X"))).StatusCode);
        var first = await CreateSegmentAsync(client, "tekil", Leaf("crm.company.city", "eq", "Bursa"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "TEKIL", Name: "Başka"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/segments/{first.Id}", Save(first) with { Code = "degisti" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/segments/{first.Id}", Save(first) with { Target = "Lead" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/segments/{first.Id}", Save(first) with { Kind = "Static", Draft = null })).StatusCode);

        // Bilinmeyen hedef, olmayan kayıt ve eski sürüm.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(client, first.Id, Guid.NewGuid(), "Include")).StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/marketing/segments/{first.Id}", Save(first) with { Version = first.Version + 5 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_archive_restore_copy_and_delete_rules()
    {
        var (client, _) = await ManagerAsync();
        await CompanyAsync(client, "Anka", "Bursa", "Tekstil");
        var segment = await CreateSegmentAsync(client, "yasam", Leaf("crm.company.city", "eq", "Bursa"));
        var active = (await (await PublishAsync(client, segment.Id)).Content.ReadFromJsonAsync<SegmentDto>())!;

        // Yayımlanmış segment silinemez; arşivlenir. Arşivdeki düzenlenemez, yayımlanamaz, üye alamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/marketing/segments/{active.Id}?version={active.Version}")).StatusCode);
        var archived = (await (await client.PostAsJsonAsync($"/api/marketing/segments/{active.Id}/archive", new SegmentActionRequest(active.Version))).Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal("Archived", archived.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/marketing/segments/{active.Id}/archive", new SegmentActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/marketing/segments/{active.Id}", Save(archived) with { Name = "Yeni ad" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(client, active.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/marketing/segments/{active.Id}")).StatusCode);

        // Arşivlenen segment geçmişte ve listede kalır; "kullanılabilir" süzgecinde görünmez.
        Assert.Contains((await client.GetFromJsonAsync<PagedResult<SegmentDto>>("/api/marketing/segments?status=Archived"))!.Items, s => s.Id == active.Id);
        Assert.DoesNotContain((await client.GetFromJsonAsync<PagedResult<SegmentDto>>("/api/marketing/segments?usable=true"))!.Items, s => s.Id == active.Id);

        var restored = (await (await client.PostAsJsonAsync($"/api/marketing/segments/{active.Id}/restore", new SegmentActionRequest())).Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal("Active", restored.Status);

        // Kopya: yeni taslak; koşul kopyalanır, sürüm ve üyeler kopyalanmaz.
        var copy = await client.PostAsJsonAsync($"/api/marketing/segments/{active.Id}/copy", new CopySegmentRequest("yasam-kopya", "Yaşam kopya"));
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var copied = (await copy.Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal(("Draft", 0, restored.Draft), (copied.Status, copied.PublishedVersion, copied.Draft));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/marketing/segments/{active.Id}/copy", new CopySegmentRequest("yasam-kopya", "Yine"))).StatusCode);

        // Hiç yayımlanmamış, üyesiz taslak silinir.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/marketing/segments/{copied.Id}?version={copied.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/marketing/segments/{copied.Id}")).StatusCode);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Another_tenant_sees_none_of_the_segments_members_or_companies()
    {
        var (clientA, _) = await ManagerAsync();
        var (clientB, _) = await ManagerAsync();
        var companyA = await CompanyAsync(clientA, "Anka", "Bursa", "Tekstil");
        var segmentA = await CreateSegmentAsync(clientA, "ortak-kod", Leaf("crm.company.city", "eq", "Bursa"));
        await PublishAsync(clientA, segmentA.Id);
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(clientA, segmentA.Id, companyA.PartyId!.Value, "Include")).StatusCode);

        // B'nin segmentleri boş; A'nın kimlikleri B'de bulunamaz; sayılar yalnızca B'nin verisidir.
        Assert.Empty((await clientB.GetFromJsonAsync<PagedResult<SegmentDto>>("/api/marketing/segments"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/marketing/segments/{segmentA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/marketing/segments/{segmentA.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"/api/marketing/segments/{segmentA.Id}/publish", new SegmentActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsync($"/api/marketing/segments/{segmentA.Id}/refresh", null)).StatusCode);
        Assert.Equal(0, await CountAsync(clientB, Leaf("crm.company.city", "eq", "Bursa")));

        // B kendi segmentine A'nın firmasını ekleyemez; aynı kod B'de serbesttir (benzersizlik tenant ile birlikte).
        var segmentB = await CreateSegmentAsync(clientB, "ortak-kod", null, kind: "Static");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(clientB, segmentB.Id, companyA.PartyId!.Value, "Include")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(clientB, segmentA.Id, companyA.PartyId!.Value, "Include")).StatusCode);
        Assert.Empty((await clientB.GetFromJsonAsync<PagedResult<SegmentMemberDto>>($"/api/marketing/segment-members?segmentId={segmentA.Id}"))!.Items);
        Assert.Empty((await clientB.GetFromJsonAsync<PagedResult<SegmentVersionDto>>($"/api/marketing/segment-versions?segmentId={segmentA.Id}"))!.Items);
    }

    // ---- Yetki ----

    [Fact]
    public async Task Fields_of_a_closed_module_or_without_read_permission_are_unavailable_and_cannot_leak_membership()
    {
        var tenant = await TenantAsync();
        var admin = await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions]);
        var noCrm = await UserClientAsync(tenant, AllMarketing);
        await CompanyAsync(admin, "Anka", "Bursa", "Tekstil");

        var available = (await admin.GetFromJsonAsync<SegmentFieldsResponse>("/api/marketing/segment-fields?target=Company"))!;
        Assert.Contains(available.Fields, f => f.Key == "crm.company.city");
        Assert.Contains(available.Fields.First(f => f.Key == "crm.company.createdAt").Operators, o => o == "lastdays");
        // Fırsat okuma izni olmayan kişiye fırsat ilişkileri sunulmaz (neden açıklanır); firma alanları sunulur.
        Assert.Contains(available.Unavailable, u => u.Key == "crm.company.openOpportunity");
        Assert.DoesNotContain(available.Unavailable, u => u.Key == "crm.company.city");

        // CRM okuma izni olmayan kişi: alanlar kullanılamaz (nedeniyle), o alanla önizleme ve sayı alınamaz.
        var restricted = (await noCrm.GetFromJsonAsync<SegmentFieldsResponse>("/api/marketing/segment-fields?target=Company"))!;
        Assert.Empty(restricted.Fields);
        Assert.Contains(restricted.Unavailable, u => u.Key == "crm.company.city" && u.Reason.Length > 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(noCrm, "Company", Leaf("crm.company.city", "eq", "Bursa"))).StatusCode);
        var segment = await CreateSegmentAsync(noCrm, "izinsiz", Leaf("crm.company.city", "eq", "Bursa"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PublishAsync(noCrm, segment.Id)).StatusCode);

        // Modül tenant için kapanınca alanlar yine kullanılamaz; veriler silinmez.
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: false);
        Assert.Empty((await admin.GetFromJsonAsync<SegmentFieldsResponse>("/api/marketing/segment-fields?target=Company"))!.Fields);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(admin, "Company", Leaf("crm.company.city", "eq", "Bursa"))).StatusCode);
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        Assert.Equal(1, await CountAsync(admin, Leaf("crm.company.city", "eq", "Bursa")));
    }

    [Fact]
    public async Task Counts_and_previews_respect_the_callers_record_scope()
    {
        var tenant = await TenantAsync();
        var manager = await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions]);
        var own = await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions], RecordScope.Own);
        await CompanyAsync(manager, "Yönetici firması", "Bursa", "Tekstil");
        await CompanyAsync(own, "Kendi firması", "Bursa", "Tekstil");

        Assert.Equal(2, await CountAsync(manager, Leaf("crm.company.city", "eq", "Bursa")));
        var scoped = await PreviewAsync(own, "Company", Leaf("crm.company.city", "eq", "Bursa"));
        var body = (await scoped.Content.ReadFromJsonAsync<SegmentEvaluationDto>())!;
        Assert.Equal(1, body.Count);
        Assert.Equal("Kendi firması", Assert.Single(body.Rows).Label);

        // Kapsamı dar kişi, başkasının segmentini göremez.
        var segment = await CreateSegmentAsync(manager, "yonetici", Leaf("crm.company.city", "eq", "Bursa"));
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/marketing/segments/{segment.Id}")).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<SegmentDto>>("/api/marketing/segments"))!.Items);
    }

    [Fact]
    public async Task Each_action_needs_its_own_permission()
    {
        var tenant = await TenantAsync();
        var manager = await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions]);
        var viewer = await UserClientAsync(tenant, [SegmentsRead, CrmModule.Permissions.CompaniesRead]);
        var writer = await UserClientAsync(tenant, [SegmentsRead, SegmentsWrite, CrmModule.Permissions.CompaniesRead]);
        var nobody = await UserClientAsync(tenant, [CrmModule.Permissions.CompaniesRead]);
        await CompanyAsync(manager, "Anka", "Bursa", "Tekstil");
        var segment = await CreateSegmentAsync(manager, "izinler", Leaf("crm.company.city", "eq", "Bursa"));
        await PublishAsync(manager, segment.Id);

        // İzleyici sayı alır ve okur; yazamaz, yayımlayamaz.
        Assert.Equal(1, await CountAsync(viewer, Leaf("crm.company.city", "eq", "Bursa")));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/marketing/segments/{segment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "x1", Name: "X"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/marketing/segments/{segment.Id}/copy", new CopySegmentRequest("x2", "X"))).StatusCode);

        // Yazan oluşturur ve düzenler; yayımlayamaz, yenileyemez, arşivleyemez.
        var mine = await CreateSegmentAsync(writer, "yazan", Leaf("crm.company.city", "eq", "Bursa"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(writer, mine.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsync($"/api/marketing/segments/{mine.Id}/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"/api/marketing/segments/{mine.Id}/archive", new SegmentActionRequest())).StatusCode);
        Assert.Equal("Draft", (await manager.GetFromJsonAsync<SegmentDto>($"/api/marketing/segments/{mine.Id}"))!.Status);

        // Pazarlama izni olmayan hiçbir uca giremez; giriş yapmamış 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/marketing/segments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/marketing/segment-fields?target=Company")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(nobody, "Company", Leaf("crm.company.city", "eq", "Bursa"))).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/marketing/segments")).StatusCode);

        // Pazarlama modülü tenant için kapalıysa herkes 403 alır.
        await factory.SetModuleAsync(tenant.Id, MarketingModule.ModuleKey, enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/marketing/segments")).StatusCode);
    }

    // ---- Yardımcılar ----

    private static JsonObject Leaf(string field, string op, params string[] values) => new()
    {
        ["field"] = field,
        ["operator"] = op,
        ["values"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]),
    };

    private static JsonObject And(params JsonObject[] items) => new() { ["op"] = "and", ["items"] = new JsonArray([.. items.Select(i => (JsonNode?)i)]) };

    private static JsonObject Or(params JsonObject[] items) => new() { ["op"] = "or", ["items"] = new JsonArray([.. items.Select(i => (JsonNode?)i)]) };

    private static IEnumerable<string> Errors(JsonObject? problem) => problem!["errors"]!.AsObject().Select(e => e.Key);

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client, string target, JsonObject condition) =>
        client.PostAsJsonAsync("/api/marketing/segments/preview", new PreviewSegmentRequest(target, condition));

    private static async Task<int> CountAsync(HttpClient client, JsonObject condition)
    {
        var response = await PreviewAsync(client, "Company", condition);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SegmentEvaluationDto>())!.Count;
    }

    private static SaveSegmentRequest Save(SegmentDto s) =>
        new(s.Version, s.Code, s.Name, s.Description, s.Target, s.Kind, s.Purpose, s.OwnerId, s.Draft);

    private static async Task<SegmentDto> CreateSegmentAsync(HttpClient client, string code, JsonObject? draft, string kind = "Dynamic")
    {
        var response = await client.PostAsJsonAsync("/api/marketing/segments",
            new SaveSegmentRequest(Code: code, Name: $"Segment {code}", Target: "Company", Kind: kind, Purpose: "Test", Draft: draft?.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SegmentDto>())!;
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"/api/marketing/segments/{id}/publish", new SegmentActionRequest());

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient client, Guid segmentId, Guid targetId, string kind, string? reason = null) =>
        client.PostAsJsonAsync("/api/marketing/segment-members", new SaveSegmentMemberRequest(SegmentId: segmentId, TargetId: targetId, Kind: kind, Reason: reason));

    private static async Task<CompanyDto> CompanyAsync(HttpClient client, string name, string? city, string industry)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name, City: city, Industry: industry));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task ContactAsync(HttpClient client, Guid companyId, string first, string last) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last, CompanyId: companyId))).StatusCode);

    private async Task<Modulix.Core.Platform.Tenant> TenantAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        await factory.SetModuleAsync(tenant.Id, MarketingModule.ModuleKey, enabled: true);
        return tenant;
    }

    private async Task<(HttpClient Client, Guid TenantId)> ManagerAsync()
    {
        var tenant = await TenantAsync();
        return (await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions]), tenant.Id);
    }

    private async Task<HttpClient> UserClientAsync(Modulix.Core.Platform.Tenant tenant, string[] permissions, RecordScope scope = RecordScope.All)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, scope, user);
        return await LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
