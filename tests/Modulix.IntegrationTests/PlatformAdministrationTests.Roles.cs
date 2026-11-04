using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Platform;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 5: platform rolleri (Destek, Operasyon, Finans, Owner) ve platform hesapları için iki adımlı doğrulama zorunluluğu.
/// Rol kaydı boş olan eski platform yöneticisi Owner sayılır (geriye uyum). Rolü olmayan yazma ucu Owner'a özeldir; hesap hiç
/// silinmez, rolleri boşaltmak yalnızca platform erişimini kapatır.
/// </summary>
public partial class PlatformAdministrationTests
{
    private async Task<ModulixUser> CreatePlatformMemberAsync(PlatformRole roles)
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        stored.PlatformRoles = roles;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    [Fact]
    public async Task Each_platform_role_can_only_do_its_own_writes_and_everyone_can_read()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        using var support = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Support));
        using var operations = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Operations));
        using var finance = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Finance));
        using var owner = await LoginAsync(await CreatePlatformAdministratorAsync()); // rol kaydı boş: Owner sayılır
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);

        // Okuma her platform rolüne açıktır; ekip listesi yalnızca Owner'a.
        foreach (var client in new[] { support, operations, finance, owner })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/overview")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/platform/tenants/{tenant.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/audit")).StatusCode);
        }

        foreach (var client in new[] { support, operations, finance })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/platform/team")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/platform/team")).StatusCode);

        // Destek: izler, olay işler ve 2FA sıfırlar; firma ve lisans yazamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/modules/crm", new SetTenantModuleRequest(false, Reason: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/limits/seats", new SetLimitOverrideRequest(9, tomorrow, "x"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync("/api/platform/events/deliveries/retry", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.PostAsJsonAsync("/api/platform/users/reset-two-factor", new ResetTwoFactorRequest("yok@test.modulix"))).StatusCode);

        // Operasyon: firma ve modül; plan ve limit yok.
        Assert.Equal(HttpStatusCode.NoContent, (await operations.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Operasyon"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await operations.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await operations.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/modules/crm", new SetTenantModuleRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.PostAsJsonAsync("/api/platform/plans", new SavePlanRequest(NewPlanKey(), "Ad", ["crm"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest("x", "x"))).StatusCode);

        // Finans: plan, abonelik, limit ve modül koşulları; firma durumu yok.
        var plan = await CreatePlanAsync(finance, NewPlanKey(), ["crm"], seats: 10);
        Assert.Equal(HttpStatusCode.NoContent, (await finance.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest(plan.Key, "Finans"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await finance.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/limits/seats", new SetLimitOverrideRequest(20, tomorrow, "Finans"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/close", new TenantReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/administrators", new InviteTenantAdministratorRequest(Email(), "x"))).StatusCode);

        // Owner hepsini yapar.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Owner"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);

        // Reddedilen her işlem hiçbir şey yazmadı: firma açık, Destek'in denemeleri denetim kaydında yok.
        var audit = (await owner.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&pageSize=50"))!;
        Assert.DoesNotContain(audit.Items, i => i.Reason == "x");
    }

    [Fact]
    public async Task Every_platform_write_endpoint_has_an_explicit_role_rule()
    {
        // Yeni bir yazma ucu eklenince rol tablosuna da yazılmalı; aksi halde yalnızca Owner'a açık kalır (varsayılan ret).
        var endpoints = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/platform", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Method: m, Pattern: e.RoutePattern.RawText)))
            .Where(x => !HttpMethods.IsGet(x.Method) && !HttpMethods.IsHead(x.Method) && !HttpMethods.IsOptions(x.Method))
            .Select(x => PlatformAccessRules.Key(x.Method, x.Pattern))
            .ToList();

        Assert.NotEmpty(endpoints);
        var missing = endpoints.Where(k => !PlatformAccessRules.DefinedWrites.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0, $"Rol tablosunda olmayan platform yazma uçları: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Owner_manages_the_team_and_removing_access_never_deletes_the_account()
    {
        var tenant = await factory.CreateTenantAsync();
        var person = await factory.CreateUserAsync(tenant); // firmanın kullanıcısı; platform erişimi yok
        var ownerUser = await CreatePlatformAdministratorAsync();
        using var owner = await LoginAsync(ownerUser);
        using var personClient = await LoginAsync(person);
        Assert.Equal(HttpStatusCode.Forbidden, (await personClient.GetAsync("/api/platform/overview")).StatusCode);

        // Kayıtlı hesap e-postasıyla Destek olarak eklenir; erişim bir sonraki istekte açılır.
        var added = await owner.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest(person.Email!, ["support"]));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var member = (await added.Content.ReadFromJsonAsync<PlatformTeamMemberDto>())!;
        Assert.Equal(["Support"], member.Roles);
        Assert.Equal(HttpStatusCode.OK, (await personClient.GetAsync("/api/platform/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await personClient.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("x"))).StatusCode);

        // Roller toplanır; Operasyon eklenince askıya alabilir.
        var updated = await owner.PutAsJsonAsync($"/api/platform/team/{person.Id}", new SetPlatformRolesRequest(["Support", "Operations"]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(["Support", "Operations"], (await updated.Content.ReadFromJsonAsync<PlatformTeamMemberDto>())!.Roles);
        Assert.Equal(HttpStatusCode.NoContent, (await personClient.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Ekip üyesi"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);

        // Rolleri boşaltmak platform erişimini kapatır; hesap ve firma üyeliği olduğu gibi kalır.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/platform/team/{person.Id}", new SetPlatformRolesRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await personClient.GetAsync("/api/platform/overview")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == person.Id);
            Assert.False(stored.IsPlatformAdministrator);
            Assert.Equal(PlatformRole.None, stored.PlatformRoles);
            Assert.True(await db.Memberships.AsNoTracking().AnyAsync(m => m.UserId == person.Id && m.TenantId == tenant.Id));
        }

        Assert.Equal(HttpStatusCode.OK, (await personClient.GetAsync("/api/tenant/current")).StatusCode); // firma erişimi etkilenmedi

        // Her değişiklik platform denetim kaydındadır.
        var audit = (await owner.GetFromJsonAsync<PlatformAuditPageResponse>("/api/platform/audit?action=platform.team-changed&pageSize=50"))!;
        Assert.Equal(3, audit.Items.Count(i => i.Details!.Contains(person.Id.ToString())));
        Assert.All(audit.Items.Where(i => i.Details!.Contains(person.Id.ToString())), i => Assert.Equal(ownerUser.Id, i.ActorUserId));
    }

    [Fact]
    public async Task Team_changes_are_validated_owner_roles_are_protected_and_non_owners_cannot_manage_the_team()
    {
        var ownerUser = await CreatePlatformAdministratorAsync();
        var otherOwner = await CreatePlatformAdministratorAsync();
        var finance = await CreatePlatformMemberAsync(PlatformRole.Finance);
        var person = await factory.CreateUserAsync();
        using var owner = await LoginAsync(ownerUser);
        using var financeClient = await LoginAsync(finance);

        // Doğrulama: Owner arayüzden verilemez, bilinmeyen rol, boş e-posta, kayıtsız hesap.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest(person.Email!, ["Owner"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest(person.Email!, ["Yonetici"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest(" ", ["Support"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest("yok@test.modulix", ["Support"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/platform/team/{Guid.NewGuid()}", new SetPlatformRolesRequest(["Support"]))).StatusCode);

        // Owner'ın rolü ve kendi rolü arayüzden değiştirilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/platform/team/{otherOwner.Id}", new SetPlatformRolesRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/platform/team/{ownerUser.Id}", new SetPlatformRolesRequest(["Support"]))).StatusCode);

        // Finans ekibi yönetemez, kendine rol ekleyemez, başkasını ekleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await financeClient.PostAsJsonAsync("/api/platform/team", new AddPlatformMemberRequest(person.Email!, ["Support"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await financeClient.PutAsJsonAsync($"/api/platform/team/{finance.Id}", new SetPlatformRolesRequest(["Support", "Operations", "Finance"]))).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/team")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(PlatformRole.Finance, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == finance.Id)).PlatformRoles);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == person.Id)).IsPlatformAdministrator);
        Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == otherOwner.Id)).IsPlatformAdministrator);
    }

    [Fact]
    public async Task Platform_endpoints_refuse_accounts_without_two_factor_when_it_is_required()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var strict = factory.WithWebHostBuilder(builder => builder.UseSetting("Platform:RequireTwoFactor", "true"));
        using var client = strict.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(admin.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var me = (await login.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.True(me.IsPlatformAdministrator);
        Assert.Equal(["Owner"], me.PlatformRoles); // rol kaydı boş eski yönetici Owner sayılır

        // İki adımlı doğrulama açık değil: okuma da yazma da reddedilir ve nedeni arayüze bildirilir.
        var refused = await client.GetAsync("/api/platform/overview");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("two-factor-required", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/platform/plans", new SavePlanRequest(NewPlanKey(), "Ad", ["crm"]))).StatusCode);

        // Doğrulama açılınca (oturum sürerken) bir sonraki istekte erişim açılır.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            Assert.True((await users.SetTwoFactorEnabledAsync((await users.FindByIdAsync(admin.Id.ToString()))!, true)).Succeeded);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/overview")).StatusCode);

        // Doğrulama olmadan hiçbir plan yazılmadı.
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<ModulixDbContext>().Plans.AnyAsync(p => p.Name == "Ad"));
    }
}
