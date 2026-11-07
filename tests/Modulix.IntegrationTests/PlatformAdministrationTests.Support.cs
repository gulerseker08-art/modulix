using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using Modulix.Shared.Users;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 7 (Y1.3): destek erişimi. Gerekçeli, süreli, tek firmayla sınırlı ve salt okunur; başlangıç/bitiş ve kapanış
/// denetlenir, firma yöneticilerine e-posta gider; süresi dolan ya da geri alınan erişim bir sonraki istekte reddedilir.
/// </summary>
public partial class PlatformAdministrationTests
{
    private async Task<(HttpClient Client, Modulix.Infrastructure.Identity.ModulixUser User)> LoginSupportAsync(PlatformRole roles = PlatformRole.Support)
    {
        var user = await CreatePlatformMemberAsync(roles);
        return (await LoginAsync(user), user);
    }

    private async Task<(Modulix.Core.Platform.Tenant Tenant, HttpClient TenantUser)> TenantWithDataAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite], user);
        var client = await LoginAsync(user);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Müşteri Verisi Ltd."))).StatusCode);
        return (tenant, client);
    }

    [Fact]
    public async Task Support_access_is_read_only_audited_and_visible_to_tenant_administrators()
    {
        var (tenant, tenantUser) = await TenantWithDataAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        var (support, supportUser) = await LoginSupportAsync();
        using var tenantClient = tenantUser;
        using var supportClient = support;

        // Erişim yokken platform hesabı firma verisini göremez, firmayı seçemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);

        var grant = await supportClient.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/support-access", new GrantSupportAccessRequest("Müşteri kayıt hatası bildirdi", 30));
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var granted = (await grant.Content.ReadFromJsonAsync<SupportAccessDto>())!;
        Assert.Equal(SupportAccessStateDto.Active, granted.State);
        Assert.Equal(30, Math.Round((granted.ExpiresAt - granted.GrantedAt).TotalMinutes));

        // Firmayı seçer, kayıtları okur; yazamaz, kullanıcıları ve denetimi göremez.
        Assert.Equal(HttpStatusCode.OK, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);
        var companies = (await supportClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!;
        Assert.Equal("Müşteri Verisi Ltd.", Assert.Single(companies.Items).Name);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Yazılamaz Ltd."))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/audit")).StatusCode);
        // Rol yalnızca açık modüllerin okuma izinlerini taşır (dışa aktarma, yazma, kullanıcı ve denetim izinleri yok).
        using (var roleScope = factory.OpenScope(tenant.Id))
        {
            var role = (await roleScope.ServiceProvider.GetRequiredService<Modulix.Core.Authorization.IRoleService>().ListAsync(CancellationToken.None))
                .Single(r => r.Name == "Destek erişimi (salt okunur)");
            Assert.NotEmpty(role.Permissions);
            Assert.All(role.Permissions, p => Assert.EndsWith(".read", p));
            Assert.DoesNotContain(role.Permissions, p => p.StartsWith("core.", StringComparison.Ordinal));
        }

        Assert.Single((await tenantClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items); // veri değişmedi

        // Destek kullanıcısı firmanın kullanıcı listesinde ve koltuk sayımında yok; platform onu platform ekranından görür.
        using var adminClient = await LoginAsync(admin);
        var users = (await adminClient.GetFromJsonAsync<UserPageResponse>("/api/users"))!;
        Assert.DoesNotContain(users.Items, u => u.Email == supportUser.Email);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var seats = Assert.Single((await PlanOf(platform, tenant.Id)).Limits, l => l.Key == "seats");
        Assert.Equal(2, seats.Usage); // yalnızca firmanın iki gerçek üyesi

        // Firma yöneticisi erişimi görür ve e-posta alır; denetim kayıtları başlangıcı ve gerekçeyi taşır.
        var tenantView = (await adminClient.GetFromJsonAsync<List<SupportAccessDto>>("/api/tenant/support-access"))!;
        Assert.Equal(granted.Id, Assert.Single(tenantView).Id);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var mail = await db.EmailMessages.AsNoTracking().Where(e => e.Reference == $"core.support-access:{granted.Id}").ToListAsync();
            Assert.Contains(mail, m => m.ToAddress == admin.Email);
            Assert.Contains("salt okunur", mail[0].HtmlBody);
            Assert.DoesNotContain(mail, m => m.ToAddress == supportUser.Email);
        }

        var audit = await platform.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.support-access-granted");
        var entry = Assert.Single(audit!.Items);
        Assert.Equal("Müşteri kayıt hatası bildirdi", entry.Reason);
        Assert.Equal(supportUser.Id, entry.ActorUserId);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.True(await db.AuditLog.AsNoTracking().AnyAsync(a => a.EntityKey == "core.tenant" && a.RecordId == tenant.Id && a.UserId == supportUser.Id));
        }
    }

    [Fact]
    public async Task Expired_or_revoked_support_access_is_refused_on_the_next_request_and_history_is_kept()
    {
        var (tenant, tenantUser) = await TenantWithDataAsync();
        var (support, supportUser) = await LoginSupportAsync();
        using var supportClient = support;
        using var tenantClient = tenantUser;
        var granted = (await (await supportClient.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/support-access", new GrantSupportAccessRequest("Süre sınaması", 15)))
            .Content.ReadFromJsonAsync<SupportAccessDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/crm/companies")).StatusCode);

        // Süre dolar (bitiş geçmişe alınır): oturum sürse de bir sonraki istek reddedilir; kayıt silinmez, "Süresi doldu" görünür.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE platform.SupportAccessGrants SET ExpiresAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE Id = {granted.Id}");
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/tenant/current")).StatusCode);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var listed = (await platform.GetFromJsonAsync<List<SupportAccessDto>>($"/api/platform/support-access?tenantId={tenant.Id}"))!;
        Assert.Equal(SupportAccessStateDto.Expired, Assert.Single(listed).State);
        Assert.Empty((await platform.GetFromJsonAsync<List<SupportAccessDto>>($"/api/platform/support-access?tenantId={tenant.Id}&activeOnly=true"))!);

        // Süresi dolmuş erişim yeniden açılabilir (yeni kayıt); firma yöneticisi (kullanıcı yönetimi izniyle) erken kapatabilir.
        var second = (await (await supportClient.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/support-access", new GrantSupportAccessRequest("İkinci erişim", 60)))
            .Content.ReadFromJsonAsync<SupportAccessDto>())!;
        Assert.NotEqual(granted.Id, second.Id);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/crm/companies")).StatusCode);

        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var adminClient = await LoginAsync(tenantAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PostAsync($"/api/tenant/support-access/{second.Id}/revoke?reason=Gerek%20kalmad%C4%B1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsync($"/api/tenant/support-access/{second.Id}/revoke", null)).StatusCode);

        // Geçmiş korunur: iki kayıt var, ikincisi kapatılmış; denetimde kapanış yazılı.
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var history = await db2.SupportAccessGrants.AsNoTracking().Where(g => g.TenantId == tenant.Id).OrderBy(g => g.GrantedAt).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("Gerek kalmadı", history[1].EndReason);
        Assert.Equal(tenantAdmin.Id, history[1].EndedByUserId);
        var revokedAudit = await platform.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.support-access-revoked");
        Assert.Single(revokedAudit!.Items);
        Assert.True(await db2.Memberships.AsNoTracking().AnyAsync(m => m.UserId == supportUser.Id && m.TenantId == tenant.Id && m.Status == MembershipStatus.Support));
    }

    [Fact]
    public async Task Support_access_is_validated_and_limited_to_the_one_tenant_it_was_granted_for()
    {
        var (tenantA, clientA) = await TenantWithDataAsync();
        var (tenantB, clientB) = await TenantWithDataAsync();
        using var a = clientA;
        using var b = clientB;
        var (support, _) = await LoginSupportAsync();
        using var supportClient = support;

        // Doğrulama: gerekçe zorunlu, süre 15-480 dakika, olmayan firma 404.
        var url = $"/api/platform/tenants/{tenantA.Id}/support-access";
        Assert.Equal(HttpStatusCode.BadRequest, (await supportClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("  ", 30))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await supportClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("Neden", 5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await supportClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("Neden", 481))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await supportClient.PostAsJsonAsync($"/api/platform/tenants/{Guid.NewGuid()}/support-access", new GrantSupportAccessRequest("Neden", 30))).StatusCode);

        // Yalnızca A için açılır: B seçilemez ve B'nin verisi görünmez.
        Assert.Equal(HttpStatusCode.Created, (await supportClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("A için", 30))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await supportClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("Yine A", 30))).StatusCode); // zaten geçerli
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenantB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenantA.Id))).StatusCode);
        Assert.Single((await supportClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items);

        // Kapalı firmaya erişim açılamaz; firmanın olağan üyesine ayrıca açılmaz.
        var closedTenant = await factory.CreateTenantAsync();
        using var owner = await LoginAsync(await CreatePlatformAdministratorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/platform/tenants/{closedTenant.Id}/close", new TenantReasonRequest("Kapalı"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await supportClient.PostAsJsonAsync($"/api/platform/tenants/{closedTenant.Id}/support-access", new GrantSupportAccessRequest("Kapalı firma", 30))).StatusCode);
        var memberOnly = await factory.CreateUserAsync(tenantA); // firmanın olağan üyesi
        using (var memberScope = factory.Services.CreateScope())
        {
            var users = memberScope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Modulix.Infrastructure.Identity.ModulixUser>>();
            var stored = (await users.FindByIdAsync(memberOnly.Id.ToString()))!;
            stored.IsPlatformAdministrator = true;
            stored.PlatformRoles = PlatformRole.Support;
            Assert.True((await users.UpdateAsync(stored)).Succeeded);
        }

        using var memberClient = await LoginAsync(memberOnly);
        Assert.Equal(HttpStatusCode.Conflict, (await memberClient.PostAsJsonAsync(url, new GrantSupportAccessRequest("Zaten üye", 30))).StatusCode);

        // Firma yöneticisi başka firmanın erişimini göremez ya da kapatamaz.
        var tenantAdminB = await factory.CreateUserAsync(tenantA);
        await MakeAdministratorAsync(tenantA.Id, tenantAdminB);
        using var adminA = await LoginAsync(tenantAdminB);
        var grantId = Assert.Single((await adminA.GetFromJsonAsync<List<SupportAccessDto>>("/api/tenant/support-access"))!).Id;
        var otherAdmin = await factory.CreateUserAsync(tenantB);
        await MakeAdministratorAsync(tenantB.Id, otherAdmin);
        using var adminB = await LoginAsync(otherAdmin);
        Assert.Empty((await adminB.GetFromJsonAsync<List<SupportAccessDto>>("/api/tenant/support-access"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/tenant/support-access/{grantId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await adminA.PostAsync($"/api/tenant/support-access/{grantId}/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Only_support_operations_and_owner_roles_can_grant_and_tenant_users_cannot()
    {
        var (tenant, tenantUser) = await TenantWithDataAsync();
        using var tenantClient = tenantUser;
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var adminClient = await LoginAsync(tenantAdmin);
        using var anonymous = factory.CreateBrowserClient();
        var (finance, _) = await LoginSupportAsync(PlatformRole.Finance);
        using var financeClient = finance;
        var url = $"/api/platform/tenants/{tenant.Id}/support-access";
        var request = new GrantSupportAccessRequest("Yetki sınaması", 30);

        // Finans ve firma yöneticisi açamaz, anonim 401; hiçbir kayıt oluşmaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await financeClient.PostAsJsonAsync(url, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsJsonAsync(url, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync(url, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, request)).StatusCode);

        // Firma kullanıcısı (kullanıcı yönetimi izni olmayan) firma tarafı listeyi göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync("/api/tenant/support-access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/platform/support-access")).StatusCode);

        // Operasyon ve Owner açabilir.
        var (operations, _) = await LoginSupportAsync(PlatformRole.Operations);
        using var operationsClient = operations;
        Assert.Equal(HttpStatusCode.Created, (await operationsClient.PostAsJsonAsync(url, request)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Single(await db.SupportAccessGrants.AsNoTracking().Where(g => g.TenantId == tenant.Id).ToListAsync());
    }

    [Fact]
    public async Task Support_access_does_not_see_sensitive_fields()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var owner = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead], owner);
        using var ownerClient = await LoginAsync(owner);
        var company = (await (await ownerClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Gizli Ltd."))).Content.ReadFromJsonAsync<CompanyDto>())!;
        var opportunity = (await (await ownerClient.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Büyük iş", company.Id, Amount: 250_000m, Currency: "EUR", Probability: 60))).Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal(250_000m, (await ownerClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!.Amount);

        var (support, _) = await LoginSupportAsync();
        using var supportClient = support;
        Assert.Equal(HttpStatusCode.Created, (await supportClient.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/support-access", new GrantSupportAccessRequest("Tutar sorunu", 30))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);

        // Kayıt görünür, hassas alanlar (tutar, olasılık) hiçbir okuma yolunda gelmez; para birimi ve başlık gelir.
        var read = (await supportClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        Assert.Equal("Büyük iş", read.Title);
        Assert.Null(read.Amount);
        Assert.Null(read.Probability);
        Assert.DoesNotContain("250000", await supportClient.GetStringAsync($"/api/records/crm.opportunity/{opportunity.Id}"));
        Assert.Null(Assert.Single((await supportClient.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?companyId={company.Id}"))!.Items).Amount);
        Assert.DoesNotContain("250000", await supportClient.GetStringAsync($"/api/records/crm.opportunity/{opportunity.Id}/history"));

        // Rol da hassas alan iznini taşımaz.
        using var roleScope = factory.OpenScope(tenant.Id);
        var role = (await roleScope.ServiceProvider.GetRequiredService<Modulix.Core.Authorization.IRoleService>().ListAsync(CancellationToken.None))
            .Single(r => r.Name == "Destek erişimi (salt okunur)");
        Assert.DoesNotContain(OpportunityAmountsRead, role.Permissions);
        Assert.Contains(OpportunitiesRead, role.Permissions);
    }
}
