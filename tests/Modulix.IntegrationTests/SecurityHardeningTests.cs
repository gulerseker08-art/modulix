using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Access;
using Modulix.Shared.Account;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Güvenlik taraması (13.11.2026) düzeltmeleri: rol atamasında ve davette yetki yükseltme engeli, başka firmada da
/// kullanılan hesabın kilidini ve oturumlarını firma yöneticisinin yönetememesi, kilitli hesapta açık oturumdan şifre
/// denenememesi.
/// </summary>
public class SecurityHardeningTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task User_manager_cannot_assign_or_invite_with_a_role_broader_than_their_own()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var managerRole = await factory.CreateRoleAsync(tenant.Id, [ManageUsers, CompaniesRead], Scope.Own, manager);
        var broad = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite, ManageRoles], Scope.All);
        var narrow = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own);
        var managerMembership = await factory.GetMembershipIdAsync(manager.Id, tenant.Id);
        using var client = await LoginAsync(manager);

        // Kendisinde olmayan izin ve kapsamı taşıyan rolü kendine de veremez, davetle de veremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/memberships/{managerMembership}/roles", new SetMembershipRolesRequest([managerRole.Id, broad.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/users/invitations", new InviteUserRequest($"u-{Guid.NewGuid():N}@test.modulix", "Suç Ortağı", [broad.Id]))).StatusCode);

        // Kendinde olanla sınırlı rol verilebilir (mutlu yol).
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/memberships/{managerMembership}/roles", new SetMembershipRolesRequest([managerRole.Id, narrow.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/users/invitations", new InviteUserRequest($"u-{Guid.NewGuid():N}@test.modulix", "Yeni Kişi", [narrow.Id]))).StatusCode);
    }

    [Fact]
    public async Task Administrator_cannot_unlock_or_sign_out_an_account_used_in_another_tenant()
    {
        var tenantA = await factory.CreateTenantAsync();
        var tenantB = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenantA);
        await factory.MakeAdministratorAsync(tenantA.Id, admin);
        var shared = await factory.CreateUserAsync(tenantA, tenantB);
        var local = await factory.CreateUserAsync(tenantA);
        await LockAsync(shared);
        await LockAsync(local);
        using var client = await LoginAsync(admin);

        // Hesap B firmasında da kullanılıyor: A'nın yöneticisi küresel kilidi sıfırlayamaz, B'deki oturumları kapatamaz.
        var sharedMembership = await factory.GetMembershipIdAsync(shared.Id, tenantA.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/users/{sharedMembership}/unlock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/users/{sharedMembership}/close-sessions", null)).StatusCode);

        // Yalnızca bu firmaya ait hesapta işlem yapılır.
        var localMembership = await factory.GetMembershipIdAsync(local.Id, tenantA.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{localMembership}/unlock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{localMembership}/close-sessions", null)).StatusCode);
    }

    [Fact]
    public async Task Locked_account_rejects_even_the_correct_current_password_from_an_open_session()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user);
        await LockAsync(user);

        var response = await client.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(ModulixApiFactory.Password, "Yeni.Sifre.12345"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task LockAsync(ModulixUser user)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        Assert.True((await users.SetLockoutEndDateAsync(stored, DateTimeOffset.UtcNow.AddMinutes(15))).Succeeded);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
