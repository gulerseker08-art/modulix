using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y9c: sektör profilleri. Platform yöneticisi firmayı profille açar (modüller, alanlar ve listeler hazır); profil mevcut
/// firmaya da uygulanır ve yalnızca ekler: firmanın alanları ve değerleri değişmez, yeniden uygulamada eksik kalmaz.
/// </summary>
public class SectorProfileTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Logistics_profile_opens_a_tenant_with_modules_fields_and_lists_ready()
    {
        var platformAdmin = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(platformAdmin);
        using var client = await LoginAsync(platformAdmin);

        var profiles = (await client.GetFromJsonAsync<List<SectorProfileDto>>("/api/platform/profiles"))!;
        Assert.Equal(["general", "logistics", "clinic", "manufacturer"], profiles.Select(p => p.Key));
        Assert.Equal(["crm", "logistics"], profiles.Single(p => p.Key == "logistics").Modules);

        var invalid = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Profilsiz", $"p-{Guid.NewGuid():N}"[..20], ["crm"], [], $"y-{Guid.NewGuid():N}@test.modulix", "Yonetici", "yok"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("profile", await invalid.Content.ReadAsStringAsync());

        var response = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Yol Lojistik", $"l-{Guid.NewGuid():N}"[..20], ["crm", "logistics"], [], $"y-{Guid.NewGuid():N}@test.modulix", "Yonetici", "logistics"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>())!.Tenant.Tenant;
        Assert.Equal(["crm", "logistics"], tenant.EnabledModules);
        Assert.Equal("logistics", tenant.ProfileKey);
        Assert.NotNull(tenant.ProfileAppliedAt);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var fields = await db.CustomFields.AsNoTracking().Where(f => f.EntityKey == CrmEntityKeys.Opportunity).OrderBy(f => f.SortOrder).ToListAsync();
            Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], fields.Select(f => f.Label));
            Assert.Equal(["Tır", "Kamyon", "Kamyonet", "Frigorifik"], fields[1].Options.Select(o => o.Label));

            var sources = await db.ChoiceLists.AsNoTracking().SingleAsync(l => l.ListKey == CrmLists.CompanySource);
            Assert.Equal("Yük borsası", sources.Values[^1].Label);

            // Açılış ve profil firmanın denetim kaydındadır; profil "Sektör profili" alanıyla.
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.tenant").OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal([AuditAction.Created, AuditAction.Updated], audit.Select(a => a.Action));
            Assert.Contains("Lojistik firması", Regex.Unescape(audit[1].Changes));
            Assert.All(audit, a => Assert.Equal(platformAdmin.Id, a.UserId));
        }

        // Profil yeniden uygulanınca eklenecek bir şey yoktur.
        var again = (await client.GetFromJsonAsync<ProfilePlanDto>($"/api/platform/tenants/{tenant.Id}/profile/logistics"))!;
        Assert.True(again.IsEmpty);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/platform/tenants/{tenant.Id}/profile/yok")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/platform/tenants/{Guid.NewGuid()}/profile/logistics")).StatusCode);
    }

    [Fact]
    public async Task Platform_administrator_applies_a_profile_to_an_existing_tenant_and_opens_its_modules()
    {
        var (tenant, _, _) = await SetUpAsync();
        var platformAdmin = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(platformAdmin);
        using var client = await LoginAsync(platformAdmin);

        var preview = (await client.GetFromJsonAsync<ProfilePlanDto>($"/api/platform/tenants/{tenant.Id}/profile/logistics"))!;
        Assert.Equal(["Lojistik"], preview.ModulesToEnable);
        Assert.Empty(preview.UnavailableModules);

        var applied = await client.PostAsync($"/api/platform/tenants/{tenant.Id}/profile/logistics", null);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var detail = (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!;
        Assert.Equal(["crm", "logistics"], detail.Tenant.EnabledModules);
        Assert.Equal("logistics", detail.Tenant.ProfileKey);
    }

    [Fact]
    public async Task Applying_a_profile_only_adds_and_never_changes_the_tenants_fields_or_values()
    {
        var (tenant, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        // Firmanın kendi "araç tipi" alanı (farklı seçeneklerle) ve adını değiştirdiği aşama.
        Assert.Equal(HttpStatusCode.Created, (await adminClient.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(
            CrmEntityKeys.Opportunity, "araç tipi", CustomFieldType.Choice, Options: [new(null, "Kendi aracımız"), new(null, "Taşeron")]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync($"/api/customization/lists/{CrmLists.OpportunityStage}", new SaveChoiceListRequest(
        [
            new("Lead", "Ön Görüşme"), new("Qualified", "Nitelikli"), new("Proposal", "Teklif"), new("Negotiation", "Pazarlık"),
            new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"),
        ]))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/customization/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsync("/api/customization/profiles/clinic/apply", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync("/api/customization/profiles/yok/preview")).StatusCode);

        // Firma yöneticisi modül açamaz: Lojistik kapalı kalır, CRM'deki alanlar eklenir; aynı adlı alan atlanır.
        var logistics = (await adminClient.GetFromJsonAsync<ProfilePlanDto>("/api/customization/profiles/logistics/preview"))!;
        Assert.Empty(logistics.ModulesToEnable);
        Assert.Equal(["Lojistik"], logistics.UnavailableModules);
        Assert.Equal(["Güzergâh", "Yük tipi"], logistics.Fields.Select(f => f.Label));

        var clinic = (await adminClient.GetFromJsonAsync<ProfilePlanDto>("/api/customization/profiles/clinic/preview"))!;
        Assert.Equal(["Doğum tarihi", "Sigorta", "Tedavi türü"], clinic.Fields.Select(f => f.Label));
        Assert.Equal(["Tedavi planı", "Muayene", "Kontrol"], clinic.ListValues.Select(v => v.Label));

        var response = await adminClient.PostAsync("/api/customization/profiles/clinic/apply", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(clinic.ListValues, (await response.Content.ReadFromJsonAsync<ProfilePlanDto>())!.ListValues);
        response = await adminClient.PostAsync("/api/customization/profiles/logistics/apply", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Mevcut alan ve değer olduğu gibi; yeni aşama açık aşamaların sonuna eklenir.
        var fields = (await adminClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!;
        var own = fields.Single(f => f.Label == "araç tipi");
        Assert.Equal(["Kendi aracımız", "Taşeron"], own.Options.Select(o => o.Label));
        Assert.Equal(["araç tipi", "Tedavi türü", "Güzergâh", "Yük tipi"], fields.Where(f => f.EntityKey == CrmEntityKeys.Opportunity).Select(f => f.Label));
        var lists = (await adminClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!;
        Assert.Equal(
            ["Ön Görüşme", "Nitelikli", "Teklif", "Pazarlık", "Tedavi planı", "Kazanıldı", "Kaybedildi"],
            lists.Single(l => l.Key == CrmLists.OpportunityStage).Values.Select(v => v.Label));
        Assert.Equal(CrmLists.StageCategories.Open, lists.Single(l => l.Key == CrmLists.OpportunityStage).Values[4].Category);
        Assert.Equal(["Muayene", "Kontrol"], lists.Single(l => l.Key == CrmLists.ActivityType).Values.Where(v => !v.BuiltIn).Select(v => v.Label));

        // Firmanın profili en son uygulanandır; yeniden uygulamada eklenecek bir şey kalmaz.
        var state = (await adminClient.GetFromJsonAsync<SectorProfilesResponse>("/api/customization/profiles"))!;
        Assert.Equal("logistics", state.Current);
        Assert.True((await adminClient.GetFromJsonAsync<ProfilePlanDto>("/api/customization/profiles/clinic/preview"))!.IsEmpty);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.tenant").OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal(2, audit.Count);
            Assert.All(audit, a => Assert.Equal(admin.Id, a.UserId));
            Assert.Contains("Klinik", Regex.Unescape(audit[0].Changes));
        }

        // Başka firma etkilenmez.
        var (_, otherAdmin, _) = await SetUpAsync();
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
        Assert.Null((await otherClient.GetFromJsonAsync<SectorProfilesResponse>("/api/customization/profiles"))!.Current);
        Assert.Equal("Aday", (await otherClient.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!
            .Single(l => l.Key == CrmLists.OpportunityStage).Values[0].Label);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Rep)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead], rep);
        return (tenant, admin, rep);
    }

    private async Task SetPlatformAdministratorAsync(ModulixUser user)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
