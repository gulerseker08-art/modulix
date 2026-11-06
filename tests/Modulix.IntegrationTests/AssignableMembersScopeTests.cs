using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0030: "Yeniden ata" için atanabilir üyeler kayıt türünün kapsamıyla çözülür (aktivite için <c>crm.activity</c>), başka türün
/// kapsamıyla ya da rolün varsayılanıyla değil. Platform tavanı yalnızca o türü daraltır; sunucu kapsam dışı sorumluyu ayrıca reddeder.
/// </summary>
public sealed class AssignableMembersScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Access = [ActivitiesRead, ActivitiesWrite, ActivitiesAssign, OpportunitiesRead];

    [Fact]
    public async Task Ceiling_on_activities_narrows_only_the_activity_member_list_and_the_server_rejects_the_assignment()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var actor = await LoginAsync(setup.Actor);

        // Tavan yokken iki tür de tüm üyeleri verir ve yeniden atama çalışır.
        Assert.Contains(setup.Target.Id, await AssignableAsync(actor, CrmEntityKeys.Activity));
        var activity = await CreateActivityAsync(actor);
        Assert.Equal(HttpStatusCode.OK, (await Reassign(actor, activity, setup.Target.Id)).StatusCode);

        // Tavan yalnızca aktivitede "kendi kayıtları": aktivite listesi yalnızca kişinin kendisi; fırsat listesi değişmez.
        var put = await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([new ScopeLimitDto(CrmEntityKeys.Activity, SharedScope.Own)], "Aktivite görünürlüğü kendi kayıtları"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(setup.Actor.Id, Assert.Single(await AssignableAsync(actor, CrmEntityKeys.Activity)));
        Assert.Contains(setup.Target.Id, await AssignableAsync(actor, CrmEntityKeys.Opportunity));

        // Ekran listeyi türüne göre alır; yine de sunucu kapsam dışı sorumluyu reddeder ve kayıt değişmez.
        var own = await CreateActivityAsync(actor);
        var rejected = await Reassign(actor, own, setup.Target.Id);
        Assert.False(rejected.IsSuccessStatusCode, await rejected.Content.ReadAsStringAsync());
        Assert.Equal(setup.Actor.Id, (await actor.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{own.Id}"))!.OwnerId);

        // Tavan kaldırılınca liste ve atama geri gelir.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([]))).StatusCode);
        Assert.Contains(setup.Target.Id, await AssignableAsync(actor, CrmEntityKeys.Activity));
        Assert.Equal(HttpStatusCode.OK, (await Reassign(actor, own, setup.Target.Id)).StatusCode);
    }

    [Fact]
    public async Task Assignable_list_needs_an_entity_key_and_never_lists_another_tenants_members()
    {
        var setup = await SetUpAsync();
        var other = await factory.CreateTenantAsync();
        var stranger = await factory.CreateUserAsync(other);
        using var actor = await LoginAsync(setup.Actor);

        Assert.Equal(HttpStatusCode.BadRequest, (await actor.GetAsync("/api/tenant/members?assignable=true")).StatusCode);

        var members = await AssignableAsync(actor, CrmEntityKeys.Activity);
        Assert.DoesNotContain(stranger.Id, members);
        Assert.Contains(setup.Target.Id, members);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Actor, ModulixUser Target)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var actor = await factory.CreateUserAsync(tenant);
        var target = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Access, RecordScope.All, actor, target);
        return (tenant, actor, target);
    }

    private static async Task<List<Guid>> AssignableAsync(HttpClient client, string entityKey) =>
        (await client.GetFromJsonAsync<List<TenantMemberResponse>>($"/api/tenant/members?assignable=true&entityKey={entityKey}"))!
            .Select(m => m.UserId).ToList();

    private static async Task<ActivityDto> CreateActivityAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Yeniden atanacak iş"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<ActivityDto>())!;
    }

    private static Task<HttpResponseMessage> Reassign(HttpClient client, ActivityDto activity, Guid ownerId) =>
        client.PostAsJsonAsync($"/api/crm/activities/{activity.Id}/reassign", new ReassignActivityRequest(activity.Version, ownerId));

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
