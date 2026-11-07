using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using Modulix.Shared.Users;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 4: plan, abonelik ve koltuk limiti. Hiçbir şey silinmez: plan kapatılır, abonelik kapatılır, istisna geri alınır,
/// plan değişince kapanan modülün verisi yerinde kalır. Koltuk sınırı yalnızca yeni davet ve yeniden etkinleştirmeyi durdurur;
/// mevcut kullanıcılar etkilenmez. Bir firmanın planı ve limiti başka firmayı etkilemez.
/// </summary>
public partial class PlatformAdministrationTests
{
    private async Task<PlanDto> CreatePlanAsync(HttpClient platform, string key, string[] modules, int? seats = null, bool active = true)
    {
        var limits = seats is { } s ? new Dictionary<string, int> { ["seats"] = s } : null;
        var response = await platform.PostAsJsonAsync("/api/platform/plans", new SavePlanRequest(key, $"Plan {key}", modules, limits, active));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanDto>())!;
    }

    private static string NewPlanKey() => $"p-{Guid.NewGuid():N}"[..20];

    private static async Task<TenantPlanResponse> PlanOf(HttpClient platform, Guid tenantId) =>
        (await platform.GetFromJsonAsync<TenantPlanResponse>($"/api/platform/tenants/{tenantId}/plan"))!;

    [Fact]
    public async Task Assigning_a_plan_opens_its_modules_and_changing_it_closes_the_old_ones_without_deleting_data()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite], user);
        using var client = await LoginAsync(user);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var crmPlan = await CreatePlanAsync(platform, NewPlanKey(), ["crm"], seats: 5);
        var otherPlan = await CreatePlanAsync(platform, NewPlanKey(), ["support"]);

        // Plan atanınca modül Plan kaynağıyla açılır; abonelik ve limit görünür.
        var until = DateTimeOffset.UtcNow.AddDays(90);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest(crmPlan.Key, "Satış sözleşmesi", until))).StatusCode);
        var detail = (await platform.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!;
        var crm = Assert.Single(detail.Entitlements!, e => e.ModuleKey == "crm");
        Assert.True(crm.IsEnabled);
        Assert.Equal(LicenseSource.Plan, crm.Source);
        Assert.Equal(until.ToUnixTimeSeconds(), crm.ValidUntil!.Value.ToUnixTimeSeconds());
        var overview = await PlanOf(platform, tenant.Id);
        Assert.Equal(crmPlan.Key, overview.Subscription!.PlanKey);
        var seats = Assert.Single(overview.Limits, l => l.Key == "seats");
        Assert.Equal((5, LimitSourceDto.Plan, 1), (seats.Limit, seats.Source, seats.Usage));

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Veri Ltd."))).StatusCode);

        // Başka plana geçince CRM kapanır (erişim sınırı), kayıt ise yerinde kalır.
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest(otherPlan.Key, "Plan değişti"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/companies")).StatusCode);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.Equal(1, await db.Set<Modulix.Modules.Crm.Domain.Company>().AsNoTracking().CountAsync());
        }

        // Eski abonelik silinmedi, kapandı; yeni plan geçerli ve limit sınırsız.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var history = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenant.Id).OrderBy(s => s.StartsAt).ToListAsync();
            Assert.Equal(2, history.Count);
            Assert.NotNull(history[0].EndedAt);
            Assert.Null(history[1].EndedAt);
        }

        Assert.Null(Assert.Single((await PlanOf(platform, tenant.Id)).Limits, l => l.Key == "seats").Limit);

        // Plana geri dönünce modül yeniden açılır ve veri hemen görünür.
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest(crmPlan.Key, "Geri döndü"))).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items);

        var audit = await platform.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.plan-assigned");
        Assert.Equal(3, audit!.Items.Count);
        Assert.Contains(audit.Items, i => i.Reason == "Plan değişti");
    }

    [Fact]
    public async Task Seat_limit_stops_new_invitations_and_reactivation_but_never_touches_existing_users()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        var member = await factory.CreateUserAsync(tenant);
        var memberId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(admin);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var plan = await CreatePlanAsync(platform, NewPlanKey(), ["crm"], seats: 3);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest(plan.Key, "Başlangıç"))).StatusCode);

        // 3 koltuktan ikisi yönetici ve üye: bir davet sığar (davet bekleyen de koltuk ayırır), ikincisi reddedilir.
        var first = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "Birinci", []));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        _ = (await first.Content.ReadFromJsonAsync<InviteUserResponse>())!.User.MembershipId;
        var blocked = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "İkinci", []));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("koltuğu sınırına ulaşıldı (3/3)", await blocked.Content.ReadAsStringAsync());

        // Mevcut kullanıcılar sınır dolu olsa da etkilenmez; devre dışı bırakınca koltuk boşalır, geri açmak yer ister.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{memberId}/disable", null)).StatusCode);
        var second = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "Ayrılan yerine", []));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/users/{memberId}/enable", null)).StatusCode);

        // İstisna geçici yer açar; geri alınınca (kimse silinmeden) yeni davetler yeniden durur.
        var until = DateTimeOffset.UtcNow.AddDays(10);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/limits/seats", new SetLimitOverrideRequest(5, until, "Pilot genişletme"))).StatusCode);
        var overridden = Assert.Single((await PlanOf(platform, tenant.Id)).Limits, l => l.Key == "seats");
        Assert.Equal((5, LimitSourceDto.Override, 3), (overridden.Limit, overridden.Source, overridden.Usage));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{memberId}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "Dördüncü", []))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/limits/seats/revoke", new RevokeLimitOverrideRequest("Pilot bitti"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "Beşinci", []))).StatusCode);
        var afterRevoke = Assert.Single((await PlanOf(platform, tenant.Id)).Limits, l => l.Key == "seats");
        Assert.Equal((3, LimitSourceDto.Plan, 5), (afterRevoke.Limit, afterRevoke.Source, afterRevoke.Usage)); // limit aşıldı ama 5 kullanıcıdan hiçbiri kapanmadı

        // İstisna kaydı silinmedi, geri alındı olarak saklanır.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().LimitOverrides.AsNoTracking().SingleAsync(o => o.TenantId == tenant.Id);
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal("Pilot genişletme", stored.Reason);

        // Platform yöneticisinin kurtarma daveti koltuk sınırına takılmaz.
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/administrators", new InviteTenantAdministratorRequest(Email(), "Kurtarma"))).StatusCode);
    }

    [Fact]
    public async Task Plan_and_limit_inputs_are_validated_and_a_closed_plan_cannot_be_assigned()
    {
        var tenant = await factory.CreateTenantAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var key = NewPlanKey();
        await CreatePlanAsync(platform, key, ["crm"], seats: 3);

        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/plans", new SavePlanRequest(key, "Aynı anahtar", ["crm"]))).StatusCode);
        foreach (var bad in new[]
        {
            new SavePlanRequest("Büyük Harf", "Ad", ["crm"]),
            new SavePlanRequest(NewPlanKey(), " ", ["crm"]),
            new SavePlanRequest(NewPlanKey(), "Ad", ["yok"]),
            new SavePlanRequest(NewPlanKey(), "Ad", ["core"]),
            new SavePlanRequest(NewPlanKey(), "Ad", ["crm"], new Dictionary<string, int> { ["seats"] = -1 }),
            new SavePlanRequest(NewPlanKey(), "Ad", ["crm"], new Dictionary<string, int> { ["depolama"] = 1 }),
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/plans", bad)).StatusCode);
        }

        var url = $"/api/platform/tenants/{tenant.Id}/plan";
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url, new AssignPlanRequest(key, " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url, new AssignPlanRequest(key, "x", DateTimeOffset.UtcNow.AddDays(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync(url, new AssignPlanRequest("yok-plan", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{Guid.NewGuid()}/plan", new AssignPlanRequest(key, "x"))).StatusCode);
        Assert.Null((await PlanOf(platform, tenant.Id)).Subscription);

        // Plan kapatılır (silinmez); kapalı plan atanamaz, mevcut abone etkilenmez.
        var plans = (await platform.GetFromJsonAsync<PlansResponse>("/api/platform/plans"))!.Plans;
        var stored = plans.Single(p => p.Key == key);
        var closed = await platform.PutAsJsonAsync($"/api/platform/plans/{stored.Id}", new SavePlanRequest(key, "Kapalı plan", ["crm"], stored.Limits, IsActive: false));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PutAsJsonAsync(url, new AssignPlanRequest(key, "x"))).StatusCode);
        Assert.Contains(key, (await platform.GetFromJsonAsync<PlansResponse>("/api/platform/plans"))!.Plans.Select(p => p.Key));

        // Limit istisnası: gerekçe, gelecekte bir bitiş, eksi olmayan değer ve bilinen limit ister; geri alınacak istisna yoksa çakışma.
        var limits = $"/api/platform/tenants/{tenant.Id}/limits/seats";
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(limits, new SetLimitOverrideRequest(3, tomorrow, " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(limits, new SetLimitOverrideRequest(3, DateTimeOffset.UtcNow.AddDays(-1), "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(limits, new SetLimitOverrideRequest(-1, tomorrow, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/limits/depolama", new SetLimitOverrideRequest(3, tomorrow, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync($"{limits}/revoke", new RevokeLimitOverrideRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{Guid.NewGuid()}/limits/seats", new SetLimitOverrideRequest(3, tomorrow, "x"))).StatusCode);
    }

    [Fact]
    public async Task One_tenants_plan_and_limit_do_not_affect_another_tenant()
    {
        var limited = await factory.CreateTenantAsync();
        var free = await factory.CreateTenantAsync();
        var limitedAdmin = await factory.CreateUserAsync(limited);
        var freeAdmin = await factory.CreateUserAsync(free);
        await MakeAdministratorAsync(limited.Id, limitedAdmin);
        await MakeAdministratorAsync(free.Id, freeAdmin);
        using var limitedClient = await LoginAsync(limitedAdmin);
        using var freeClient = await LoginAsync(freeAdmin);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var plan = await CreatePlanAsync(platform, NewPlanKey(), ["crm"], seats: 1);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
            $"/api/platform/tenants/{limited.Id}/plan", new AssignPlanRequest(plan.Key, "Tek koltuk"))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await limitedClient.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "A", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await freeClient.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(Email(), "B", []))).StatusCode);

        // Plansız firmanın modülleri ve limiti değişmedi.
        Assert.Null((await PlanOf(platform, free.Id)).Subscription);
        Assert.Null(Assert.Single((await PlanOf(platform, free.Id)).Limits, l => l.Key == "seats").Limit);
        Assert.DoesNotContain(
            (await platform.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{free.Id}"))!.Entitlements!,
            e => e.Source == LicenseSource.Plan);
    }

    [Fact]
    public async Task Tenant_administrators_and_anonymous_callers_cannot_manage_plans_or_limits()
    {
        var tenant = await factory.CreateTenantAsync();
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var tenantClient = await LoginAsync(tenantAdmin);
        using var anonymous = factory.CreateBrowserClient();
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);

        foreach (var client in new[] { tenantClient, anonymous })
        {
            var expected = ReferenceEquals(client, anonymous) ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await client.GetAsync("/api/platform/plans")).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync("/api/platform/plans", new SavePlanRequest(NewPlanKey(), "Ad", ["crm"]))).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/platform/tenants/{tenant.Id}/plan")).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/plan", new AssignPlanRequest("x", "x"))).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync(
                $"/api/platform/tenants/{tenant.Id}/limits/seats", new SetLimitOverrideRequest(100, tomorrow, "Kendime koltuk"))).StatusCode);
        }

        // Firma yöneticisi kendi koltuk limitini değiştiremedi: hiçbir kayıt oluşmadı.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.LimitOverrides.AnyAsync(o => o.TenantId == tenant.Id));
        Assert.False(await db.TenantSubscriptions.AnyAsync(s => s.TenantId == tenant.Id));
    }

    private static string Email() => $"koltuk-{Guid.NewGuid():N}@test.modulix";

    [Fact]
    public async Task Changing_the_modules_of_a_plan_updates_its_current_subscribers_only_and_deletes_no_data()
    {
        var subscriber = await factory.CreateTenantAsync();
        var bystander = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(subscriber);
        await factory.CreateRoleAsync(subscriber.Id, [CompaniesRead, CompaniesWrite], user);
        using var client = await LoginAsync(user);
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var shared = await CreatePlanAsync(platform, NewPlanKey(), ["crm"]);
        var other = await CreatePlanAsync(platform, NewPlanKey(), ["crm"]);
        foreach (var (tenantId, plan) in new[] { (subscriber.Id, shared), (bystander.Id, other) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await platform.PutAsJsonAsync(
                $"/api/platform/tenants/{tenantId}/plan", new AssignPlanRequest(plan.Key, "Sözleşme", DateTimeOffset.UtcNow.AddDays(60)))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Yerinde Kalır Ltd."))).StatusCode);

        // Plandan CRM çıkıp Destek eklenir: abone için CRM kapanır, Destek açılır; veri yerinde; başka plandaki firma etkilenmez.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(
            $"/api/platform/plans/{shared.Id}", new SavePlanRequest(shared.Key, shared.Name, ["support"], null, true))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/companies")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var subscriberRights = await db.ModuleEntitlements.AsNoTracking().Where(e => e.TenantId == subscriber.Id).ToListAsync();
        Assert.False(Assert.Single(subscriberRights, e => e.ModuleKey == "crm").IsEnabled);
        Assert.True(Assert.Single(subscriberRights, e => e.ModuleKey == "support").IsEnabled);
        var bystanderRights = await db.ModuleEntitlements.AsNoTracking().Where(e => e.TenantId == bystander.Id).ToListAsync();
        Assert.True(Assert.Single(bystanderRights, e => e.ModuleKey == "crm").IsEnabled);
        Assert.DoesNotContain(bystanderRights, e => e.ModuleKey == "support");
        using (var tenantScope = factory.OpenScope(subscriber.Id))
        {
            Assert.Equal(1, await tenantScope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Modulix.Modules.Crm.Domain.Company>().AsNoTracking().CountAsync());
        }
    }
}
