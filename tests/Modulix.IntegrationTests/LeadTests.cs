using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Lead yönetimi: oluşturma, kapsam, atama, durum geçişleri, aktivite, mükerrer incelemesi, fırsata dönüşüm (atomik, tek sefer)
/// ve rapor. Tüm yazımlar kayıt boru hattından geçer; yetki ve kapsam uç noktada değil boru hattında uygulanır.
/// </summary>
public class LeadTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead,
        ActivitiesRead, ActivitiesWrite, LeadsRead, LeadsCreate, LeadsUpdate, LeadsDelete, LeadsQualify, LeadsDisqualify, LeadsConvert, LeadsExport,
    ];

    [Fact]
    public async Task Manual_lead_is_created_with_defaults_and_audit()
    {
        var (client, tenantId, user) = await ClientAsync();
        using var _ = client;

        var lead = await CreateLeadAsync(client, new SaveLeadRequest("Filo bakım talebi", LastName: "Yılmaz", Email: " Ayse@Acme.test ", Phone: "0532 111 22 33"));

        Assert.StartsWith("LD-", lead.LeadNumber);
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.Equal(LeadPriority.Normal, lead.Priority);
        Assert.Equal(user.Id, lead.OwnerId);
        Assert.NotNull(lead.AssignedAt);
        Assert.Null(lead.FirstContactAt);
        Assert.Null(lead.EstimatedBudget);
        Assert.Null(lead.TargetPurchaseDate);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var row = await db.Set<Modulix.Modules.Crm.Domain.Lead>().AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        Assert.Equal("ayse@acme.test", row.NormalizedEmail);
        Assert.Equal("+905321112233", row.NormalizedPhone);
        var audit = await db.AuditLog.Where(a => a.RecordId == lead.Id).ToListAsync();
        Assert.Equal(AuditAction.Created, Assert.Single(audit).Action);
    }

    [Fact]
    public async Task Lead_needs_a_subject_and_some_identification()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;

        var noSubject = await ProblemAsync(await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest(" ", LastName: "X")));
        Assert.Contains("subject", noSubject.Errors.Keys);

        var anonymous = await ProblemAsync(await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest("Konu")));
        Assert.Contains("lastName", noSubject.Errors.Keys.Concat(anonymous.Errors.Keys));

        var badMail = await ProblemAsync(await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest("Konu", Email: "gecersiz", Country: "TUR")));
        Assert.Contains("email", badMail.Errors.Keys);
        Assert.Contains("country", badMail.Errors.Keys);
    }

    [Fact]
    public async Task Generic_endpoints_cannot_set_status_owner_or_conversion_fields()
    {
        var (client, _, user) = await ClientAsync();
        using var _c = client;
        var lead = await CreateLeadAsync(client, new SaveLeadRequest("Konu", LastName: "Kaya"));
        var other = Guid.NewGuid();

        // Genel kayıt ucu: durum, dönüşüm ve sistem alanları yok sayılır.
        var create = await client.PostAsJsonAsync($"/api/records/{CrmModule.Entities.Lead}", new RecordWriteRequest(new JsonObject
        {
            ["subject"] = "Doğrudan", ["lastName"] = "Kaya", ["status"] = "Converted", ["convertedOpportunityId"] = other.ToString(),
            ["qualifiedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal("New", created.Values["status"]!.GetValue<string>());
        Assert.Null(created.Values["convertedOpportunityId"]);
        Assert.Null(created.Values["qualifiedAt"]);

        // Tipli PUT sorumluyu da değiştiremez (atama ayrı izin ve eylemdir).
        var update = await client.PutAsJsonAsync($"/api/crm/leads/{lead.Id}",
            new SaveLeadRequest("Yeni konu", LastName: "Kaya", OwnerId: other, Version: lead.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<LeadDto>())!;
        Assert.Equal("Yeni konu", updated.Subject);
        Assert.Equal(user.Id, updated.OwnerId);
        Assert.Equal(LeadStatus.New, updated.Status);
    }

    [Fact]
    public async Task Same_email_does_not_block_a_second_lead_but_is_reported_as_possible_match()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var first = await CreateLeadAsync(client, new SaveLeadRequest("İlk ihtiyaç", LastName: "Demir", Email: "demir@firma.test"));
        var second = await CreateLeadAsync(client, new SaveLeadRequest("Farklı ihtiyaç", LastName: "Demir", Email: "DEMIR@firma.test"));
        Assert.NotEqual(first.Id, second.Id);

        var matches = (await client.GetFromJsonAsync<List<LeadMatchDto>>($"/api/crm/leads/{second.Id}/duplicates"))!;
        var match = Assert.Single(matches);
        Assert.Equal(first.Id, match.Id);
        Assert.Equal("Lead", match.Kind);
        Assert.Contains("Aynı e-posta", match.Reasons);

        // Hiçbir şey otomatik silinmez ya da birleştirilmez.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/crm/leads/{first.Id}")).StatusCode);
    }

    [Fact]
    public async Task Unauthorized_users_cannot_read_assign_or_act_on_leads()
    {
        var (admin, tenantId, _) = await ClientAsync();
        using var _a = admin;
        var lead = await CreateLeadAsync(admin, new SaveLeadRequest("Gizli", LastName: "Kaya"));
        var tenant = tenants[tenantId];

        var reader = await UserWithAsync(tenant, [LeadsRead], RecordScope.All);
        var nobody = await UserWithAsync(tenant, [CompaniesRead], RecordScope.All);
        using var readerClient = await LoginAsync(reader.Email!);
        using var nobodyClient = await LoginAsync(nobody.Email!);

        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync($"/api/crm/leads/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobodyClient.GetAsync($"/api/crm/leads/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobodyClient.GetAsync("/api/crm/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest("Konu", LastName: "A"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new SaveLeadRequest("X", LastName: "A", Version: lead.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/assign", new AssignLeadRequest(reader.Id, lead.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(lead.Version, CreateCompany: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/crm/leads/export")).StatusCode);
    }

    [Fact]
    public async Task Own_scope_hides_other_leads_and_unassigned_pool_is_visible_only_to_assigners()
    {
        var (admin, tenantId, adminUser) = await ClientAsync();
        using var _a = admin;
        var tenant = tenants[tenantId];
        var rep = await UserWithAsync(tenant, SalesPermissions.Except([LeadsExport]).ToArray(), RecordScope.Own);
        var coordinator = await UserWithAsync(tenant, [LeadsRead, LeadsCreate, LeadsUpdate, LeadsAssign], RecordScope.Own);
        using var repClient = await LoginAsync(rep.Email!);
        using var coordClient = await LoginAsync(coordinator.Email!);

        var adminsLead = await CreateLeadAsync(admin, new SaveLeadRequest("Yöneticinin", LastName: "A"));
        var myLead = await CreateLeadAsync(repClient, new SaveLeadRequest("Temsilcinin", LastName: "B"));

        // Sorumlusu boş Lead: yönetici atama izniyle bir kullanıcıya vermeden önce havuzdadır.
        var pooled = await CreateLeadAsync(coordClient, new SaveLeadRequest("Havuz", LastName: "C"));
        var unassign = await AssignRawAsync(admin, pooled.Id, adminUser.Id, pooled.Version);
        Assert.Equal(HttpStatusCode.Forbidden, unassign.StatusCode);
        await DropOwnerAsync(tenantId, pooled.Id);
        pooled = (await coordClient.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{pooled.Id}"))!;

        // Kapsam: temsilci yalnızca kendi Lead'ini görür; havuz Lead'i atama izni olmadığı için göremez.
        var repList = (await repClient.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!;
        Assert.Equal([myLead.Id], repList.Items.Select(l => l.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.GetAsync($"/api/crm/leads/{adminsLead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await repClient.GetAsync($"/api/crm/leads/{pooled.Id}")).StatusCode);

        // Koordinatör (Own kapsam + atama izni) havuzu görür; yönetici Lead'ini görmez.
        var pool = (await coordClient.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads?unassigned=true"))!;
        Assert.Equal([pooled.Id], pool.Items.Select(l => l.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await coordClient.GetAsync($"/api/crm/leads/{adminsLead.Id}")).StatusCode);

        // Kapsam kuralı (Y8): Own kapsamlı koordinatör sorumluyu kendi kapsamı dışındaki birine atayamaz; Tümü kapsamlı yönetici atar.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AssignRawAsync(coordClient, pooled.Id, rep.Id, pooled.Version)).StatusCode);
        await factory.CreateRoleAsync(tenantId, [.. SalesPermissions, LeadsAssign], adminUser);
        var assign = await AssignRawAsync(admin, pooled.Id, rep.Id, pooled.Version);
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var assigned = (await assign.Content.ReadFromJsonAsync<LeadDto>())!;
        Assert.Equal(rep.Id, assigned.OwnerId);
        Assert.NotNull(assigned.AssignedAt);
        Assert.Contains(pooled.Id, (await repClient.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items.Select(l => l.Id));
    }

    [Fact]
    public async Task Assignment_writes_old_and_new_owner_to_audit_and_repeat_does_not_duplicate()
    {
        var (admin, tenantId, adminUser) = await ClientAsync();
        using var _a = admin;
        var tenant = tenants[tenantId];
        await factory.CreateRoleAsync(tenantId, [.. SalesPermissions, LeadsAssign], adminUser);
        var rep = await UserWithAsync(tenant, SalesPermissions, RecordScope.Own);
        var lead = await CreateLeadAsync(admin, new SaveLeadRequest("Atanacak", LastName: "Kaya"));

        var first = await AssignRawAsync(admin, lead.Id, rep.Id, lead.Version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var assigned = (await first.Content.ReadFromJsonAsync<LeadDto>())!;

        // Aynı kişiye yeniden atama (tekrar deneme) değişiklik, audit ya da sürüm artışı üretmez.
        var again = await AssignRawAsync(admin, lead.Id, rep.Id, assigned.Version);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(assigned.Version, (await again.Content.ReadFromJsonAsync<LeadDto>())!.Version);

        // Eski sürümle atama çakışmadır.
        Assert.Equal(HttpStatusCode.Conflict, (await AssignRawAsync(admin, lead.Id, adminUser.Id, lead.Version)).StatusCode);

        // Kullanıcı üye değilse atama reddedilir.
        var stranger = await factory.CreateUserAsync();
        var bad = await AssignRawAsync(admin, lead.Id, stranger.Id, assigned.Version);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var updates = await db.AuditLog.Where(a => a.RecordId == lead.Id && a.Action == AuditAction.Updated).ToListAsync();
        var change = JsonNode.Parse(Assert.Single(updates).Changes)!.AsObject();
        Assert.Equal(adminUser.Id.ToString(), change["ownerId"]!["old"]!.GetValue<string>());
        Assert.Equal(rep.Id.ToString(), change["ownerId"]!["new"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_transitions_follow_the_rules()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;

        // Eksik bilgi: ihtiyaç ve iletişim yok → nitelendirilemez.
        var thin = await CreateLeadAsync(client, new SaveLeadRequest("İnce Lead", LastName: "Kaya"));
        var denied = await PostActionAsync(client, thin.Id, "qualify", new QualifyLeadRequest(thin.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        Assert.Contains("qualification", (await denied.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);

        var lead = await CreateLeadAsync(client, new SaveLeadRequest(
            "Tam Lead", LastName: "Kaya", Email: "kaya@firma.test", NeedDescription: "10 araçlık filo bakımı"));

        // Elemek için neden zorunlu.
        var noReason = await PostActionAsync(client, lead.Id, "disqualify", new DisqualifyLeadRequest(lead.Version, " "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var badReason = await PostActionAsync(client, lead.Id, "disqualify", new DisqualifyLeadRequest(lead.Version, "olmayan-neden"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badReason.StatusCode);

        // Daha sonra takip: tarih ya da vadeli açık aktivite gerekir.
        var noDate = await PostActionAsync(client, lead.Id, "nurture", new NurtureLeadRequest(lead.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDate.StatusCode);
        var past = await PostActionAsync(client, lead.Id, "nurture", new NurtureLeadRequest(lead.Version, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        var nurtured = await ReadAsync<LeadDto>(await PostActionAsync(client, lead.Id, "nurture", new NurtureLeadRequest(lead.Version, DateTimeOffset.UtcNow.AddDays(7), "Bütçe dönemi bekleniyor")));
        Assert.Equal(LeadStatus.Nurturing, nurtured.Status);
        Assert.NotNull(nurtured.FollowUpAt);

        var qualified = await ReadAsync<LeadDto>(await PostActionAsync(client, lead.Id, "qualify", new QualifyLeadRequest(nurtured.Version, "İhtiyaç netleşti")));
        Assert.Equal(LeadStatus.Qualified, qualified.Status);
        Assert.NotNull(qualified.QualifiedAt);
        Assert.Null(qualified.FollowUpAt);

        // Zaten nitelikli, tekrar nitelendirme ve geçersiz geçişler reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "qualify", new QualifyLeadRequest(qualified.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "start-contacting", new LeadVersionRequest(qualified.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "reopen", new ReopenLeadRequest(qualified.Version, "gerek yok"))).StatusCode);

        var disqualified = await ReadAsync<LeadDto>(await PostActionAsync(client, lead.Id, "disqualify", new DisqualifyLeadRequest(qualified.Version, "NoBudget", "Bütçe ayrılmadı")));
        Assert.Equal(LeadStatus.Disqualified, disqualified.Status);
        Assert.Equal("NoBudget", disqualified.DisqualificationReason);
        Assert.NotNull(disqualified.DisqualifiedAt);

        // Elenmiş Lead'e atama yapılamaz; yeniden açmak gerekçe ister.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "reopen", new ReopenLeadRequest(disqualified.Version, ""))).StatusCode);
        var reopened = await ReadAsync<LeadDto>(await PostActionAsync(client, lead.Id, "reopen", new ReopenLeadRequest(disqualified.Version, "Müşteri yeniden aradı")));
        Assert.Equal(LeadStatus.New, reopened.Status);
        Assert.Null(reopened.DisqualificationReason);
        Assert.Equal("Müşteri yeniden aradı", reopened.StatusReason);

        // Durum geçmişi audit'te eski/yeni değerleriyle durur.
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var statusChanges = (await db.AuditLog.Where(a => a.RecordId == lead.Id && a.Action == AuditAction.Updated).OrderBy(a => a.OccurredAt).ToListAsync())
            .Select(a => JsonNode.Parse(a.Changes)!["status"]).Where(n => n is not null)
            .Select(n => $"{n!["old"]!.GetValue<string>()}>{n["new"]!.GetValue<string>()}")
            .ToList();
        Assert.Equal(["New>Nurturing", "Nurturing>Qualified", "Qualified>Disqualified", "Disqualified>New"], statusChanges);
    }

    [Fact]
    public async Task Only_disqualify_permission_can_disqualify_and_reopen()
    {
        var (admin, tenantId, _) = await ClientAsync();
        using var _a = admin;
        var tenant = tenants[tenantId];
        var lead = await CreateLeadAsync(admin, new SaveLeadRequest("Eleme", LastName: "Kaya", Email: "k@k.test", NeedDescription: "ihtiyaç"));
        var qualifier = await UserWithAsync(tenant, [LeadsRead, LeadsQualify], RecordScope.All);
        using var qualifierClient = await LoginAsync(qualifier.Email!);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(qualifierClient, lead.Id, "disqualify", new DisqualifyLeadRequest(lead.Version, "NoBudget"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostActionAsync(qualifierClient, lead.Id, "qualify", new QualifyLeadRequest(lead.Version))).StatusCode);
        // Nitelendirme izni, alan düzenleme ya da atama hakkı vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await qualifierClient.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new SaveLeadRequest("X", LastName: "K", Version: lead.Version + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignRawAsync(qualifierClient, lead.Id, qualifier.Id, lead.Version + 1)).StatusCode);
    }

    [Fact]
    public async Task Activity_is_linked_to_the_lead_and_planning_does_not_set_first_contact()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var lead = await CreateLeadAsync(client, new SaveLeadRequest("Aktivite", LastName: "Kaya"));

        var call = await PostAsync<ActivityDto>(client, "/api/crm/activities", new SaveActivityRequest(
            "İlk arama", ActivityType.Call, DateTimeOffset.UtcNow.AddHours(2), LeadId: lead.Id));
        Assert.Equal(lead.Id, call.LeadId);

        var timeline = (await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?leadId={lead.Id}"))!;
        Assert.Equal([call.Id], timeline.Items.Select(a => a.Id));

        // Planlama ilk temas değildir.
        var fresh = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Null(fresh.FirstContactAt);
        Assert.Equal(LeadStatus.New, fresh.Status);

        var missing = await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Yok", LeadId: Guid.NewGuid())));
        Assert.Equal(["Lead bulunamadı."], missing.Errors["leadId"]);
    }

    [Fact]
    public async Task Convert_with_new_company_and_contact_creates_one_opportunity_and_keeps_the_trail()
    {
        var (client, tenantId, user) = await ClientAsync();
        using var _c = client;
        var lead = await QualifiedLeadAsync(client, "Yıllık sözleşme", companyName: "Delta Ltd.");
        var activity = await PostAsync<ActivityDto>(client, "/api/crm/activities", new SaveActivityRequest("Sunum", ActivityType.Meeting, DateTimeOffset.UtcNow.AddDays(1), LeadId: lead.Id));

        var preview = (await client.GetFromJsonAsync<LeadConversionPreviewDto>($"/api/crm/leads/{lead.Id}/conversion-preview"))!;
        Assert.Empty(preview.Blockers);
        Assert.Equal("Delta Ltd.", preview.CompanyName);
        Assert.Equal("Organization", preview.CompanyKind);

        var response = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CreateCompany: true, CreateContact: true, OpportunityTitle: "Delta yıllık bakım", Amount: 50_000m));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ConvertLeadResultDto>())!;
        Assert.True(result.CompanyCreated);
        Assert.True(result.ContactCreated);
        Assert.Equal(LeadStatus.Converted, result.Lead.Status);
        Assert.Equal(result.OpportunityId, result.Lead.ConvertedOpportunityId);
        Assert.Equal(result.CompanyId, result.Lead.ConvertedCompanyId);
        Assert.Equal(user.Id, result.Lead.ConvertedBy);
        Assert.NotNull(result.Lead.ConvertedAt);

        var opportunity = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{result.OpportunityId}"))!;
        Assert.Equal(lead.Id, opportunity.SourceLeadId);
        Assert.Equal(result.CompanyId, opportunity.CompanyId);
        Assert.Equal(result.ContactId, opportunity.ContactId);
        Assert.Equal(50_000m, opportunity.Amount);
        Assert.Equal("Qualified", opportunity.Stage);

        // Kişi yeni firmaya bağlıdır; firma Lead'in firma adını taşır.
        var company = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{result.CompanyId}"))!;
        Assert.Equal("Delta Ltd.", company.Name);
        var contact = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{result.ContactId}"))!;
        Assert.Equal(result.CompanyId, contact.CompanyId);

        // Aktivite kopyalanmaz/çoğaltılmaz: Lead'e bağlı kalır, fırsata bağlı aktivite yoktur.
        var leadActivities = (await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?leadId={lead.Id}"))!;
        Assert.Equal([activity.Id], leadActivities.Items.Select(a => a.Id));
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?opportunityId={result.OpportunityId}"))!.Items);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?sourceLeadId={lead.Id}"))!.TotalCount);

        // İkinci dönüşüm isteği ikinci fırsat açmaz; dönüşmüş Lead üzerinde başka eylem de yapılamaz.
        var second = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(result.Lead.Version, CreateCompany: true));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?sourceLeadId={lead.Id}"))!.TotalCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "disqualify", new DisqualifyLeadRequest(result.Lead.Version, "NoBudget"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(client, lead.Id, "reopen", new ReopenLeadRequest(result.Lead.Version, "dene"))).StatusCode);

        // Dönüşmüş Lead'in hedefleri silinemez (iz korunur).
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/crm/opportunities/{result.OpportunityId}?version={opportunity.Version}")).StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.Where(a => a.RecordId == lead.Id && a.Action == AuditAction.Updated).OrderByDescending(a => a.OccurredAt).FirstAsync();
        var changes = JsonNode.Parse(audit.Changes)!.AsObject();
        Assert.Equal(result.OpportunityId.ToString(), changes["convertedOpportunityId"]!["new"]!.GetValue<string>());
    }

    [Fact]
    public async Task Convert_with_existing_company_and_contact_leaves_their_data_untouched()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Mevcut A.Ş.", City: "Ankara", Email: "info@mevcut.test"));
        var contact = await PostAsync<ContactDto>(client, "/api/crm/contacts", new SaveContactRequest("Ali", "Veli", company.Id, Title: "Satın alma", Email: "ali@mevcut.test"));
        var lead = await QualifiedLeadAsync(client, "Ek sipariş", companyName: "Başka Ad", lastName: "Boş", email: "bos@lead.test");

        var response = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CompanyId: company.Id, ContactId: contact.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ConvertLeadResultDto>())!;
        Assert.False(result.CompanyCreated);
        Assert.False(result.ContactCreated);
        Assert.Equal(company.Id, result.CompanyId);

        // Mevcut kayıtların verisi Lead bilgisiyle ezilmedi (sürümler değişmedi).
        var companyAfter = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal(company.Version, companyAfter.Version);
        Assert.Equal("Mevcut A.Ş.", companyAfter.Name);
        Assert.Equal("info@mevcut.test", companyAfter.Email);
        var contactAfter = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{contact.Id}"))!;
        Assert.Equal(contact.Version, contactAfter.Version);
        Assert.Equal("ali@mevcut.test", contactAfter.Email);
    }

    [Fact]
    public async Task Failed_conversion_rolls_back_everything()
    {
        var (client, tenantId, _) = await ClientAsync();
        using var _c = client;
        var existing = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Aynı Ad Ltd."));
        var lead = await QualifiedLeadAsync(client, "Rollback", companyName: "Rollback Ltd.");

        // Fırsat aşaması geçersiz: firma ve kişi oluşturulduktan sonra fırsat adımı patlar → hiçbir kayıt kalmaz.
        var failed = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CreateCompany: true, CreateContact: true, ContactFirstName: "Ali", ContactLastName: "Veli", Stage: "OlmayanAsama"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);

        var companies = (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Rollback"))!;
        Assert.Empty(companies.Items);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?search=Veli"))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?sourceLeadId={lead.Id}"))!.Items);
        var unchanged = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Equal(LeadStatus.Qualified, unchanged.Status);
        Assert.Equal(lead.Version, unchanged.Version);
        Assert.Null(unchanged.ConvertedOpportunityId);

        // Geçersiz para birimi de fırsat adımında patlar; bu kez de firma ve kişi geride kalmamalıdır.
        var clash = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CreateCompany: true, CompanyName: existing.Name + " Yeni", CreateContact: true, Currency: "XX"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Aynı Ad"))!.Items);
        Assert.Equal(LeadStatus.Qualified, (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!.Status);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?search=Kaya"))!.Items);

        // Lead nitelikli değilse hiçbir şey açılmaz.
        var fresh = await CreateLeadAsync(client, new SaveLeadRequest("Nitelikli değil", LastName: "Kaya"));
        var notQualified = await client.PostAsJsonAsync($"/api/crm/leads/{fresh.Id}/convert", new ConvertLeadRequest(fresh.Version, CreateCompany: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notQualified.StatusCode);
        _ = tenantId;
    }

    [Fact]
    public async Task Convert_permission_alone_does_not_grant_record_creation()
    {
        var (admin, tenantId, _) = await ClientAsync();
        using var _a = admin;
        var tenant = tenants[tenantId];
        var lead = await QualifiedLeadAsync(admin, "Yetki", companyName: "Yetki Ltd.");
        // Dönüşüm izni var ama firma/kişi/fırsat yazma izni yok.
        var limited = await UserWithAsync(tenant, [LeadsRead, LeadsConvert, CompaniesRead, ContactsRead, OpportunitiesRead], RecordScope.All);
        using var limitedClient = await LoginAsync(limited.Email!);

        var response = await limitedClient.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(lead.Version, CreateCompany: true));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Yetki"))!.Items);
        Assert.Equal(LeadStatus.Qualified, (await admin.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!.Status);
    }

    [Fact]
    public async Task Two_concurrent_conversions_produce_a_single_opportunity()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var lead = await QualifiedLeadAsync(client, "Eşzamanlı", companyName: "Paralel Ltd.");

        var request = new ConvertLeadRequest(lead.Version, CreateCompany: true, CompanyName: "Paralel Ltd.");
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", request),
            client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", request));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?sourceLeadId={lead.Id}"))!.TotalCount);
        Assert.Single((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Paralel"))!.Items);
    }

    [Fact]
    public async Task Individual_customer_converts_without_an_organization_name()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var lead = await QualifiedLeadAsync(client, "Bireysel talep", companyName: null, lastName: "Şahin", email: "sahin@mail.test");
        lead = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;

        var result = await ReadAsync<ConvertLeadResultDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CreateCompany: true)));
        var company = (await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{result.CompanyId}"))!;
        Assert.Equal("Mehmet Şahin", company.Name);
        Assert.Equal("Individual", company.Kind);
    }

    [Fact]
    public async Task Lead_report_uses_the_period_cohort_and_never_a_misleading_rate()
    {
        var (client, _, _) = await ClientAsync();
        using var _c = client;
        var from = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");

        // Boş kohort: oran ve süre uydurulmaz.
        var empty = (await client.GetFromJsonAsync<LeadReportDto>($"/api/crm/leads/report?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"))!;
        Assert.Equal(0, empty.CohortCount);
        Assert.Null(empty.ConversionRate);
        Assert.Null(empty.AverageFirstContactHours);
        Assert.Null(empty.MedianFirstContactHours);

        await CreateLeadAsync(client, new SaveLeadRequest("Web", LastName: "A", Source: "WebForm"));
        await CreateLeadAsync(client, new SaveLeadRequest("Web 2", LastName: "B", Source: "WebForm"));
        var converted = await QualifiedLeadAsync(client, "Dönüşen", companyName: "Rapor Ltd.");
        await client.PostAsJsonAsync($"/api/crm/leads/{converted.Id}/convert", new ConvertLeadRequest(converted.Version, CreateCompany: true));
        var dq = await CreateLeadAsync(client, new SaveLeadRequest("Elenen", LastName: "D", Source: "Referral"));
        await PostActionAsync(client, dq.Id, "disqualify", new DisqualifyLeadRequest(dq.Version, "NoNeed"));

        var report = (await client.GetFromJsonAsync<LeadReportDto>($"/api/crm/leads/report?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"))!;
        Assert.Equal(4, report.CohortCount);
        Assert.Equal(2, report.BySource.Single(s => s.Key == "WebForm").Count);
        Assert.Equal(1, report.ConvertedFromCohort);
        Assert.Equal(0.25, report.ConversionRate);
        Assert.Equal(1, report.ConvertedInPeriod);
        Assert.NotNull(report.AverageHoursToConvert);
        Assert.Equal(1, report.DisqualificationReasons.Single(r => r.Key == "NoNeed").Count);
        Assert.Equal(1, report.ByStatus.Single(s => s.Key == LeadStatus.Disqualified).Count);
        Assert.False(report.Truncated);

        // Dönem dışı kohort: ayın başka bir döneminde gelen Lead sayılmaz, ama dönüşüm bu dönemde sayılır.
        var past = (await client.GetFromJsonAsync<LeadReportDto>(
            $"/api/crm/leads/report?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-10).ToString("O"))}"))!;
        Assert.Equal(0, past.CohortCount);
        Assert.Null(past.ConversionRate);
        Assert.Equal(0, past.ConvertedInPeriod);
    }

    [Fact]
    public async Task Report_and_export_respect_the_users_scope()
    {
        var (admin, tenantId, _) = await ClientAsync();
        using var _a = admin;
        var tenant = tenants[tenantId];
        var rep = await UserWithAsync(tenant, [.. SalesPermissions], RecordScope.Own);
        using var repClient = await LoginAsync(rep.Email!);
        await CreateLeadAsync(admin, new SaveLeadRequest("Yöneticinin 1", LastName: "A"));
        await CreateLeadAsync(admin, new SaveLeadRequest("Yöneticinin 2", LastName: "B"));
        await CreateLeadAsync(repClient, new SaveLeadRequest("Temsilcinin", LastName: "C"));
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));

        Assert.Equal(1, (await repClient.GetFromJsonAsync<LeadReportDto>($"/api/crm/leads/report?from={from}&to={to}"))!.CohortCount);
        Assert.Equal(3, (await admin.GetFromJsonAsync<LeadReportDto>($"/api/crm/leads/report?from={from}&to={to}"))!.CohortCount);

        var csv = await repClient.GetAsync("/api/crm/leads/export");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Temsilcinin", text);
        Assert.DoesNotContain("Yöneticinin", text);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Contains(await db.AuditLog.Where(a => a.Action == AuditAction.Exported).ToListAsync(), a => a.EntityKey == CrmModule.Entities.Lead && a.UserId == rep.Id);
    }

    [Fact]
    public async Task Other_tenant_leads_are_not_visible_and_cannot_be_linked()
    {
        var (a, _, _) = await ClientAsync();
        var (b, _, _) = await ClientAsync();
        using var _a = a;
        using var _b = b;
        var lead = await CreateLeadAsync(a, new SaveLeadRequest("A'nın Lead'i", LastName: "A"));

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/crm/leads/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(b, lead.Id, "qualify", new QualifyLeadRequest(lead.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(lead.Version, CreateCompany: true))).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);
        var link = await ProblemAsync(await b.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Bağ", LeadId: lead.Id)));
        Assert.Contains("leadId", link.Errors.Keys);
    }

    [Fact]
    public void Shared_lead_keys_match_the_module()
    {
        Assert.Equal(CrmModule.Entities.Lead, CrmEntityKeys.Lead);
        Assert.Equal(CrmModule.Lists.LeadSource, CrmLeadLists.Source);
        Assert.Equal(CrmModule.Lists.LeadDisqualificationReason, CrmLeadLists.DisqualificationReason);
        Assert.Equal(Modulix.Modules.Crm.Domain.LeadStatus.All, LeadStatus.All);
        Assert.Equal(CrmModule.ActivityOutcomes.All, [ActivityOutcome.Reached, ActivityOutcome.NoAnswer, ActivityOutcome.Sent]);
    }

    // ---- yardımcılar

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Modulix.Core.Platform.Tenant> tenants = new();

    private async Task<(HttpClient Client, Guid TenantId, ModulixUser User)> ClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, user);
        tenants[tenant.Id] = tenant;
        return (await LoginAsync(user.Email!), tenant.Id, user);
    }

    private async Task<ModulixUser> UserWithAsync(Modulix.Core.Platform.Tenant tenant, IReadOnlyCollection<string> permissions, RecordScope scope)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, scope, user);
        return user;
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<LeadDto> CreateLeadAsync(HttpClient client, SaveLeadRequest request) => PostAsync<LeadDto>(client, "/api/crm/leads", request);

    /// <summary>Nitelikli Lead: gerekli bilgiler dolu, Lead nitelendirilmiş.</summary>
    private static async Task<LeadDto> QualifiedLeadAsync(
        HttpClient client, string subject, string? companyName, string lastName = "Kaya", string email = "kaya@firma.test")
    {
        var lead = await CreateLeadAsync(client, new SaveLeadRequest(
            subject, FirstName: "Mehmet", LastName: lastName, CompanyName: companyName, Email: email, NeedDescription: "Kapsam netleşti",
            EstimatedBudget: 75_000m, Source: "Referral"));
        return await ReadAsync<LeadDto>(await PostActionAsync(client, lead.Id, "qualify", new QualifyLeadRequest(lead.Version)));
    }

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, Guid id, string action, object body) =>
        client.PostAsJsonAsync($"/api/crm/leads/{id}/{action}", body);

    private static Task<HttpResponseMessage> AssignRawAsync(HttpClient client, Guid id, Guid ownerId, long version) =>
        client.PostAsJsonAsync($"/api/crm/leads/{id}/assign", new AssignLeadRequest(ownerId, version));

    /// <summary>Atanmamış (havuz) Lead'i taklit eder: sorumluyu doğrudan veritabanından boşaltır.</summary>
    private async Task DropOwnerAsync(Guid tenantId, Guid leadId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE crm.Leads SET OwnerId = NULL, AssignedAt = NULL, Version = Version + 1 WHERE Id = {0}", leadId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
