using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Communication;
using Modulix.Shared.Crm;
using Modulix.Shared.Marketing;
using Modulix.Shared.Records;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 4: kampanya, hedef kitle, alıcı çözümleme, uygunluk ve dondurma. Dört test türü: mutlu yol, hata/doğrulama,
/// tenant izolasyonu, yetki. Gönderim yoktur (dilim 5); burada hiçbir ileti gitmez.
/// </summary>
public class CampaignAudienceTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
    ];

    private static readonly string[] AllMarketing =
        [SegmentsRead, SegmentsWrite, SegmentsPublish, RecipientsRead, CampaignsRead, CampaignsWrite, CampaignsAudience];

    private static readonly string[] Communication =
        [CoreModule.Permissions.ReadCommunication, CoreModule.Permissions.ManageCommunication, CoreModule.Permissions.SuppressCommunication];

    // ---- Mutlu yol ----

    [Fact]
    public async Task Audience_separates_targets_from_recipients_and_explains_every_exclusion()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);
        var campaign = await CampaignAsync(client, "antrepo", world.SegmentId);

        var summary = (await PreviewAsync(client, campaign.Id)).Summary;

        // Hedefler: A, B, C, D, F, G (Tekstil). Alıcısı bulunanlar A, B, F, G; C'de birincil kişi yok, D'nin kişisinin e-postası yok.
        Assert.Equal((6, 4, 4), (summary.Targets, summary.ResolvedTargets, summary.Persons));
        Assert.Equal(1, summary.Eligible);                                   // yalnızca Ayşe izinli
        Assert.Equal(3, summary.Excluded);
        Assert.Equal(1, summary.Exclusions["denied"]);                       // B: ret kaydı
        Assert.Equal(1, summary.Exclusions["shared-address"]);               // F: ortak adres
        Assert.Equal(1, summary.Exclusions["duplicate-address"]);            // G: Ayşe'nin adresi
        Assert.Equal(1, summary.Unresolved["no-matching-contact"]);          // C
        Assert.Equal(1, summary.Unresolved["no-contact-point"]);             // D
        Assert.DoesNotContain("unknown", summary.Exclusions.Keys);           // Bilinmeyen izin elenir ama burada herkesin kaydı var
    }

    [Fact]
    public async Task Person_rules_limit_and_unknown_consent_change_the_recipient_counts()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);
        var campaign = await CampaignAsync(client, "tum-kisiler", world.SegmentId);

        // Tüm kişiler: A'nın ikinci kişisi (Can, izinli) de alıcı olur; hedef sayısı aynı kalır, alıcı sayısı artar.
        await UpdateAsync(client, campaign, c => c with { RecipientRule = "AllContacts" });
        var all = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal(6, all.Targets);
        Assert.Equal(2, all.Eligible);

        // Hedef başına en çok 1 alıcı: Can elenir (sınır), Ayşe birincil olduğu için kalır.
        campaign = await GetCampaignAsync(client, campaign.Id);
        await UpdateAsync(client, campaign, c => c with { MaxPerTarget = 1 });
        var limited = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal(1, limited.Eligible);
        Assert.Equal(1, limited.Exclusions["per-target-limit"]);

        // İzin kaydı olmayan kişi (Hülya) pazarlamaya uygun sayılmaz: "bilinmiyor" nedeniyle elenir.
        var h = await CompanyAsync(client, "Hülya Tekstil", "Bursa", "Tekstil ve hazır giyim");
        await ContactAsync(client, h.Id, "Hülya", "Er", "hulya@h.test", primary: true);
        await PublishRefreshAsync(client, world.SegmentId);
        Assert.Equal(1, (await PreviewAsync(client, campaign.Id)).Summary.Exclusions["unknown"]);
    }

    [Fact]
    public async Task Role_decision_maker_and_company_point_rules_pick_the_right_people_or_say_why_not()
    {
        var (client, _) = await ManagerAsync();
        var company = await CompanyAsync(client, "Kural Tekstil", "Bursa", "Tekstil ve hazır giyim");
        var buyer = await ContactAsync(client, company.Id, "Satın", "Alma", "satinalma@k.test", primary: false, roles: ["Purchasing"]);
        var boss = await ContactAsync(client, company.Id, "Karar", "Verici", "karar@k.test", primary: false, decisionMaker: true);
        await ConsentAsync(client, "satinalma@k.test", "Granted");
        await ConsentAsync(client, "karar@k.test", "Granted");
        await PostAsync(client, "/api/crm/contact-points", new SaveContactPointRequest(company.Id, "info@k.test", "Email"));
        await ConsentAsync(client, "info@k.test", "Granted");
        var segment = await PublishedSegmentAsync(client, "kural", Leaf("crm.company.name", "eq", "Kural Tekstil"));
        var campaign = await CampaignAsync(client, "kurallar", segment.Id);

        // Birincil irtibat tanımlı değil: rastgele kişiye gidilmez, neden gösterilir.
        var primary = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal((1, 0, 0), (primary.Targets, primary.Eligible, primary.ResolvedTargets));
        Assert.Equal(1, primary.Unresolved["no-matching-contact"]);

        await UpdateAsync(client, campaign, c => c with { RecipientRule = "DecisionMaker" });
        Assert.Equal(1, (await PreviewAsync(client, campaign.Id)).Summary.Eligible);

        campaign = await GetCampaignAsync(client, campaign.Id);
        await UpdateAsync(client, campaign, c => c with { RecipientRule = "Role", RecipientRole = "Purchasing" });
        Assert.Equal(1, (await PreviewAsync(client, campaign.Id)).Summary.Eligible);

        campaign = await GetCampaignAsync(client, campaign.Id);
        await UpdateAsync(client, campaign, c => c with { RecipientRule = "Role", RecipientRole = "Finance" });
        Assert.Equal(1, (await PreviewAsync(client, campaign.Id)).Summary.Unresolved["no-matching-contact"]);

        campaign = await GetCampaignAsync(client, campaign.Id);
        await UpdateAsync(client, campaign, c => c with { RecipientRule = "CompanyPoints", RecipientRole = null });
        var points = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal((1, 0), (points.Eligible, points.Persons));             // kişi değil, firmanın genel adresi
        Assert.NotEqual(buyer.Id, boss.Id);
    }

    [Fact]
    public async Task Manual_exclusions_and_suppression_remove_targets_and_recipients_and_segment_membership_never_grants_consent()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);
        var campaign = await CampaignAsync(client, "dislama", world.SegmentId);

        // Hariç tutulan hedef kitleden çıkar (hariç tutma dahil etmenin önüne geçer).
        Assert.Equal(HttpStatusCode.Created, (await AddEntryAsync(client, campaign.Id, "Exclude", targetId: world.CompanyB.PartyId, reason: "Müşteri istemedi")).StatusCode);
        Assert.Equal(5, (await PreviewAsync(client, campaign.Id)).Summary.Targets);

        // Hard bounce: izinli olsa bile adres hiçbir amaçla gönderilmez.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", "ayse@a.test", "HardBounce", null, null))).StatusCode);
        var summary = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal(0, summary.Eligible);
        Assert.Equal(1, summary.Exclusions["suppressed:hard-bounce"]);
    }

    [Fact]
    public async Task Snapshot_freezes_the_audience_and_later_changes_never_rewrite_it()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);
        var campaign = await CampaignAsync(client, "dondur", world.SegmentId);

        var created = await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = (await created.Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;
        Assert.Equal((6, 4, 1, 3), (snapshot.TargetCount, snapshot.ResolvedTargetCount, snapshot.EligibleCount, snapshot.ExcludedCount));
        Assert.Contains("denied", snapshot.Exclusions);

        // İzin sonradan iptal edilir, yeni segment üyesi gelir: yeni önizleme değişir, dondurulmuş kayıt değişmez.
        await ConsentAsync(client, "ayse@a.test", "Denied");
        await CompanyAsync(client, "Sonradan Tekstil", "Bursa", "Tekstil ve hazır giyim");
        await PublishRefreshAsync(client, world.SegmentId);
        var now = (await PreviewAsync(client, campaign.Id)).Summary;
        Assert.Equal((7, 0), (now.Targets, now.Eligible));
        var again = (await client.GetFromJsonAsync<AudienceSnapshotDto>($"/api/marketing/audience-snapshots/{snapshot.Id}"))!;
        Assert.Equal((6, 1), (again.TargetCount, again.EligibleCount));

        // Alıcılar: yalnızca kimlikler saklanır; adlar okunurken çözülür. Durum ve neden süzülebilir.
        var eligible = (await client.GetFromJsonAsync<AudienceRecipientsPageDto>($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients?status=Eligible"))!;
        Assert.Equal("Anka A", Assert.Single(eligible.Items).Label);
        var unresolved = (await client.GetFromJsonAsync<AudienceRecipientsPageDto>($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients?status=Unresolved"))!;
        Assert.Equal(2, unresolved.TotalCount);
        var denied = (await client.GetFromJsonAsync<AudienceRecipientsPageDto>($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients?code=denied"))!;
        Assert.Equal(1, denied.TotalCount);

        // Dondurulmuş kayıt değişmez, silinemez, elle oluşturulamaz; adres ve alıcı listesi genel okumada yoktur.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/marketing/audience-snapshots/{snapshot.Id}", new SaveAudienceSnapshotRequest(snapshot.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/marketing/audience-snapshots/{snapshot.Id}?version={snapshot.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/audience-snapshots", new SaveAudienceSnapshotRequest())).StatusCode);
        var raw = await client.GetStringAsync($"/api/marketing/audience-snapshots/{snapshot.Id}");
        Assert.DoesNotContain("@", raw);
        Assert.DoesNotContain("recipients", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Hata ve doğrulama ----

    [Fact]
    public async Task Invalid_campaigns_and_audience_entries_are_rejected()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("Boşluk Var", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("rolsuz", "Rolsuz") with { RecipientRule = "Role" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("sinir", "Sınır") with { MaxPerTarget = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("tarih", "Tarih") with { StartsOn = new DateOnly(2026, 12, 1), EndsOn = new DateOnly(2026, 11, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("kanal", "Kanal") with { Channel = "Telgraf" })).StatusCode);

        var campaign = await CampaignAsync(client, "dogrula", null);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/marketing/campaigns", Save("DOGRULA", "Başka"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", ToSave(campaign) with { Code = "degisti" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", ToSave(campaign) with { Channel = "Sms" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", ToSave(campaign) with { Target = "Lead" })).StatusCode);

        // Hedef kitle boşken önizleme ve dondurma reddedilir; hiçbir kayıt yazılmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<AudienceSnapshotDto>>("/api/marketing/audience-snapshots"))!.Items);

        // Girdi kuralları: tam biri dolu, aynı hedef türü, arşivlenen segment yok, mükerrer yok, elle hedef bulunmalı.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(client, campaign.Id, "Include")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(client, campaign.Id, "Include", segmentId: world.SegmentId, targetId: world.CompanyA.PartyId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(client, campaign.Id, "Include", targetId: Guid.NewGuid())).StatusCode);
        var personSegment = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "kisiler", Name: "Kişiler", Target: "Person", Kind: "Static"));
        var personSegmentId = (await personSegment.Content.ReadFromJsonAsync<SegmentDto>())!.Id;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(client, campaign.Id, "Include", segmentId: personSegmentId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddEntryAsync(client, campaign.Id, "Include", segmentId: world.SegmentId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AddEntryAsync(client, campaign.Id, "Exclude", segmentId: world.SegmentId)).StatusCode);

        // Etkin olmayan (yayımlanmamış) segment hedef kitle hesabında kullanılamaz.
        var draft = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "taslak", Name: "Taslak", Target: "Company", Kind: "Dynamic", Draft: Leaf("crm.company.city", "eq", "Bursa").ToJsonString()));
        var draftId = (await draft.Content.ReadFromJsonAsync<SegmentDto>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await AddEntryAsync(client, campaign.Id, "Include", segmentId: draftId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
    }

    [Fact]
    public async Task Archived_campaigns_cannot_be_edited_or_frozen_and_used_campaigns_cannot_be_deleted()
    {
        var (client, _) = await ManagerAsync();
        var world = await WorldAsync(client);
        var campaign = await CampaignAsync(client, "arsiv", world.SegmentId);

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/marketing/campaigns/{campaign.Id}?version={campaign.Version}")).StatusCode);
        var archived = (await (await client.PostAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}/archive", new SegmentActionRequest(campaign.Version))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal("Archived", archived.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}/archive", new SegmentActionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", ToSave(archived) with { Name = "Yeni ad" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AddEntryAsync(client, campaign.Id, "Exclude", targetId: world.CompanyB.PartyId)).StatusCode);

        var restored = (await (await client.PostAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}/restore", new SegmentActionRequest())).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal("Draft", restored.Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).StatusCode);

        var empty = await CampaignAsync(client, "bos", null);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/marketing/campaigns/{empty.Id}?version={empty.Version}")).StatusCode);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Another_tenant_sees_none_of_the_campaigns_snapshots_or_recipients()
    {
        var (clientA, _) = await ManagerAsync();
        var (clientB, _) = await ManagerAsync();
        var world = await WorldAsync(clientA);
        var campaign = await CampaignAsync(clientA, "ortak-kod", world.SegmentId);
        var snapshot = (await (await clientA.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;

        Assert.Empty((await clientB.GetFromJsonAsync<PagedResult<CampaignDto>>("/api/marketing/campaigns"))!.Items);
        Assert.Empty((await clientB.GetFromJsonAsync<PagedResult<AudienceSnapshotDto>>("/api/marketing/audience-snapshots"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/marketing/campaigns/{campaign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients")).StatusCode);

        // B kendi kampanyasına A'nın segmentini ya da firmasını ekleyemez; aynı kod B'de serbesttir.
        var own = await CampaignAsync(clientB, "ortak-kod", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(clientB, own.Id, "Include", segmentId: world.SegmentId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(clientB, own.Id, "Include", targetId: world.CompanyA.PartyId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddEntryAsync(clientB, campaign.Id, "Include", targetId: world.CompanyA.PartyId)).StatusCode);

        // A'nın izni B'nin kararını etkilemez: B'de aynı adres bilinmiyor.
        var decision = (await (await clientB.PostAsJsonAsync("/api/communication/check", new CheckCommunicationRequest("Email", "Marketing", "ayse@a.test"))).Content.ReadFromJsonAsync<CommunicationDecisionDto>())!;
        Assert.Equal("unknown", decision.Code);
    }

    // ---- Yetki ----

    [Fact]
    public async Task Each_action_needs_its_own_permission_and_recipients_need_contact_access()
    {
        var tenant = await TenantAsync();
        var manager = await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions, .. Communication]);
        var viewer = await UserClientAsync(tenant, [CampaignsRead, SegmentsRead, .. CrmPermissions, CoreModule.Permissions.ReadCommunication]);
        var writer = await UserClientAsync(tenant, [CampaignsRead, CampaignsWrite, SegmentsRead, .. CrmPermissions]);
        var noContacts = await UserClientAsync(tenant, [CampaignsRead, CampaignsAudience, SegmentsRead, CrmModule.Permissions.CompaniesRead]);
        var nobody = await UserClientAsync(tenant, [CrmModule.Permissions.CompaniesRead]);
        var world = await WorldAsync(manager);
        var campaign = await CampaignAsync(manager, "izinler", world.SegmentId);
        var snapshot = (await (await manager.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;

        // İzleyici sayıları görür; yazamaz, donduramaz, arşivleyemez.
        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/marketing/campaigns", Save("izleyici", "İzleyici"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}/archive", new SegmentActionRequest())).StatusCode);

        // Yazan oluşturur ve hedef kitle girdisi ekler; dondurma ayrı izindir. Dondurulmuş kayıt genel uçtan yazılamaz.
        var mine = await CampaignAsync(writer, "yazan", world.SegmentId);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsync($"/api/marketing/campaigns/{mine.Id}/audience/snapshot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PutAsJsonAsync($"/api/marketing/audience-snapshots/{snapshot.Id}", new SaveAudienceSnapshotRequest(snapshot.Version))).StatusCode);

        // Kişi kayıtlarını okuyamayan kişi alıcıları çözemez (sayı bile verilmez); alıcı listesi ayrıca alıcı görüntüleme izni ister.
        var denied = await noContacts.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/marketing/audience-snapshots/{snapshot.Id}/recipients")).StatusCode);

        // Pazarlama izni olmayan hiçbir uca giremez; giriş yapmamış 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/marketing/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/marketing/campaigns")).StatusCode);

        // CRM kapalıysa alıcılar çözülemez ve nedeni söylenir; veriler silinmez.
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: false);
        Assert.NotEqual(HttpStatusCode.OK, (await manager.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null)).StatusCode);
    }

    [Fact]
    public async Task Shared_access_keys_match_the_marketing_module_including_campaigns()
    {
        var modulePermissions = ((Modulix.Core.Contracts.Modules.IModule)new MarketingModule()).Permissions.Select(p => p.Key).Order().ToList();
        Assert.Equal(
            modulePermissions,
            typeof(MarketingAccess).GetFields().Where(f => f.Name != nameof(MarketingAccess.ModuleKey)).Select(f => (string)f.GetValue(null)!).Order());
        await Task.CompletedTask;
    }

    // ---- Yardımcılar ----

    private sealed record World(SegmentDto Segment, CompanyDto CompanyA, CompanyDto CompanyB)
    {
        public Guid SegmentId => Segment.Id;
    }

    /// <summary>
    /// A (Ayşe birincil + Can; ikisi de izinli), B (Deniz; ret), C (kişi yok), D (kişi var, e-postası yok), F (ortak adres), G (Ayşe'nin adresi, başka kişi).
    /// Hepsi "Tekstil" sektöründe; "Gıda" firması segmentte değildir.
    /// </summary>
    private async Task<World> WorldAsync(HttpClient client)
    {
        var a = await CompanyAsync(client, "Anka A", "Bursa", "Tekstil ve hazır giyim");
        var b = await CompanyAsync(client, "Bora B", "İzmir", "Tekstil ve hazır giyim");
        await CompanyAsync(client, "Cem C", "Kocaeli", "Tekstil ve hazır giyim");
        var d = await CompanyAsync(client, "Deniz D", "Bursa", "Tekstil ve hazır giyim");
        await CompanyAsync(client, "Gıda E", "Bursa", "Gıda ve içecek");
        var f = await CompanyAsync(client, "Fatma F", "Bursa", "Tekstil ve hazır giyim");
        var g = await CompanyAsync(client, "Gül G", "Bursa", "Tekstil ve hazır giyim");

        await ContactAsync(client, a.Id, "Ayşe", "Demir", "ayse@a.test", primary: true);
        await ContactAsync(client, a.Id, "Can", "Kaya", "can@a.test", primary: false);
        await ContactAsync(client, b.Id, "Deniz", "Er", "deniz@b.test", primary: true);
        await ContactAsync(client, d.Id, "Ece", "Su", null, primary: true);
        await ContactAsync(client, f.Id, "Fatma", "Öz", "ortak@f.test", primary: true, shared: true);
        await ContactAsync(client, g.Id, "Gül", "Ak", "AYSE@a.test", primary: true);

        await ConsentAsync(client, "ayse@a.test", "Granted");
        await ConsentAsync(client, "can@a.test", "Granted");
        await ConsentAsync(client, "deniz@b.test", "Denied");
        await ConsentAsync(client, "ortak@f.test", "Granted");

        var segment = await PublishedSegmentAsync(client, "tekstil", Leaf("crm.company.industry", "eq", "Tekstil ve hazır giyim"));
        return new World(segment, a, b);
    }

    private static JsonObject Leaf(string field, string op, params string[] values) => new()
    {
        ["field"] = field,
        ["operator"] = op,
        ["values"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]),
    };

    private static async Task<SegmentDto> PublishedSegmentAsync(HttpClient client, string code, JsonObject condition)
    {
        var created = await client.PostAsJsonAsync("/api/marketing/segments",
            new SaveSegmentRequest(Code: code, Name: $"Segment {code}", Target: "Company", Kind: "Dynamic", Draft: condition.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var segment = (await created.Content.ReadFromJsonAsync<SegmentDto>())!;
        var published = await client.PostAsJsonAsync($"/api/marketing/segments/{segment.Id}/publish", new SegmentActionRequest());
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (await published.Content.ReadFromJsonAsync<SegmentDto>())!;
    }

    private static async Task PublishRefreshAsync(HttpClient client, Guid segmentId) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/marketing/segments/{segmentId}/refresh", null)).StatusCode);

    private static SaveCampaignRequest Save(string code, string name) =>
        new(Code: code, Name: name, Target: "Company", Channel: "Email", RecipientRule: "PrimaryContact");

    private static SaveCampaignRequest ToSave(CampaignDto c) =>
        new(c.Version, c.Code, c.Name, c.Description, c.Purpose, c.Target, c.Channel, c.RecipientRule, c.RecipientRole, c.MaxPerTarget, c.StartsOn, c.EndsOn, c.OwnerId);

    private static async Task<CampaignDto> CampaignAsync(HttpClient client, string code, Guid? segmentId)
    {
        var response = await client.PostAsJsonAsync("/api/marketing/campaigns", Save(code, $"Kampanya {code}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var campaign = (await response.Content.ReadFromJsonAsync<CampaignDto>())!;
        if (segmentId is { } id)
        {
            Assert.Equal(HttpStatusCode.Created, (await AddEntryAsync(client, campaign.Id, "Include", segmentId: id)).StatusCode);
        }

        return campaign;
    }

    private static Task<CampaignDto> GetCampaignAsync(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<CampaignDto>($"/api/marketing/campaigns/{id}")!;

    private static async Task UpdateAsync(HttpClient client, CampaignDto campaign, Func<SaveCampaignRequest, SaveCampaignRequest> change)
    {
        var response = await client.PutAsJsonAsync($"/api/marketing/campaigns/{campaign.Id}", change(ToSave(campaign)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> AddEntryAsync(
        HttpClient client, Guid campaignId, string mode, Guid? segmentId = null, Guid? targetId = null, string? reason = null) =>
        client.PostAsJsonAsync("/api/marketing/campaign-audience", new SaveCampaignAudienceEntryRequest(CampaignId: campaignId, Mode: mode, SegmentId: segmentId, TargetId: targetId, Reason: reason));

    private static async Task<AudiencePreviewDto> PreviewAsync(HttpClient client, Guid campaignId)
    {
        var response = await client.PostAsync($"/api/marketing/campaigns/{campaignId}/audience/preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AudiencePreviewDto>())!;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client, string name, string? city, string industry)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name, City: city, Industry: industry));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<ContactDto> ContactAsync(
        HttpClient client, Guid companyId, string first, string last, string? email, bool primary, bool shared = false, bool decisionMaker = false, string[]? roles = null)
    {
        var response = await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var contact = (await response.Content.ReadFromJsonAsync<ContactDto>())!;
        await PostAsync(client, "/api/crm/company-contacts", new SaveCompanyContactRequest(companyId, contact.Id, Roles: roles, IsPrimary: primary, IsDecisionMaker: decisionMaker));
        if (email is not null)
        {
            await PostAsync(client, "/api/crm/person-contact-points", new SavePersonContactPointRequest(contact.Id, email, "Email", IsShared: shared));
        }

        return contact;
    }

    private static async Task PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task ConsentAsync(HttpClient client, string address, string status)
    {
        var request = new SaveConsentRequest("Email", address, "Marketing", status, "manual", "test-kaniti", null, null, null);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", request)).StatusCode);
    }

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
        return (await UserClientAsync(tenant, [.. AllMarketing, .. CrmPermissions, .. Communication]), tenant.Id);
    }

    private async Task<HttpClient> UserClientAsync(Modulix.Core.Platform.Tenant tenant, string[] permissions)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, RecordScope.All, user);
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
