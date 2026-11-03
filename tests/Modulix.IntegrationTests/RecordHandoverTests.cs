using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y2b: devre dışı bırakılan kişinin CRM kayıtlarının başka bir üyeye devri (ADR-0004). Devir kayıt boru hattından
/// geçer; kapanmış fırsat ve tamamlanmış aktivite eski sorumlusunda kalır.
/// </summary>
public class RecordHandoverTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Disabling_with_a_receiver_hands_over_companies_and_open_work()
    {
        var (tenant, admin, leaver, receiver) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var leaverMembership = await factory.GetMembershipIdAsync(leaver.Id, tenant);
        var receiverMembership = await factory.GetMembershipIdAsync(receiver.Id, tenant);

        var company = await CreateAsync(client, "crm.company", new JsonObject { ["name"] = "Devir A.Ş.", ["ownerId"] = Id(leaver) });
        await CreateAsync(client, "crm.company", new JsonObject { ["name"] = "Devir Ltd.", ["ownerId"] = Id(leaver) });
        var openDeal = await CreateAsync(client, "crm.opportunity",
            new JsonObject { ["title"] = "Açık iş", ["companyId"] = company.ToString(), ["ownerId"] = Id(leaver) });
        // Kapanmış fırsat doğrudan kapalı aşamada açılamaz (ADR-0017): açılır, sonra kazan eylemiyle kapatılır.
        var wonDeal = await CreateAsync(client, "crm.opportunity",
            new JsonObject { ["title"] = "Kazanılan", ["companyId"] = company.ToString(), ["ownerId"] = Id(leaver) });
        var wonRecord = await client.GetFromJsonAsync<Modulix.Shared.Crm.OpportunityDto>($"/api/crm/opportunities/{wonDeal}");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/crm/opportunities/{wonDeal}/win",
            new Modulix.Shared.Crm.WinOpportunityRequestDto(wonRecord!.Version, WonAmount: 1m))).StatusCode);
        var openTask = await CreateAsync(client, "crm.activity", new JsonObject { ["subject"] = "Ara", ["ownerId"] = Id(leaver) });
        // Açık Lead de devredilir (sorumlu alanı genel uçlardan değişmese de devir nedeniyle değişebilir).
        var openLead = await CreateAsync(client, "crm.lead", new JsonObject { ["subject"] = "Devredilecek Lead", ["lastName"] = "Kaya", ["ownerId"] = Id(leaver) });
        var doneTask = await CreateAsync(client, "crm.activity",
            new JsonObject { ["subject"] = "Arandı", ["completedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["ownerId"] = Id(leaver) });

        var owned = await client.GetFromJsonAsync<List<OwnedRecordsDto>>($"/api/users/{leaverMembership}/owned-records");
        Assert.Equal([2, 1, 1, 1], owned!.Select(o => o.Count));
        Assert.Equal(["firma", "açık fırsat", "açık aktivite", "açık müşteri adayı"], owned!.Select(o => o.Label));

        var disabled = await client.PostAsJsonAsync($"/api/users/{leaverMembership}/disable", new DisableUserRequest(receiverMembership));
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);

        Assert.Equal(UserStatus.Disabled, (await client.GetFromJsonAsync<UserDto>($"/api/users/{leaverMembership}"))!.Status);
        Assert.Equal(Id(receiver), await OwnerAsync(client, "crm.company", company));
        Assert.Equal(Id(receiver), await OwnerAsync(client, "crm.opportunity", openDeal));
        Assert.Equal(Id(receiver), await OwnerAsync(client, "crm.activity", openTask));
        Assert.Equal(Id(receiver), await OwnerAsync(client, "crm.lead", openLead));
        Assert.Equal(Id(leaver), await OwnerAsync(client, "crm.opportunity", wonDeal));
        Assert.Equal(Id(leaver), await OwnerAsync(client, "crm.activity", doneTask));

        owned = await client.GetFromJsonAsync<List<OwnedRecordsDto>>($"/api/users/{leaverMembership}/owned-records");
        Assert.All(owned!, o => Assert.Equal(0, o.Count));

        // Her devir kaydın kendi denetim kaydına, işlemi yapan adına yazılır.
        using var scope = factory.OpenScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.RecordId == company && a.EntityKey == "crm.company").ToListAsync();
        Assert.Contains(audit, a => a.UserId == admin.Id && a.Changes.Contains(Id(receiver)));
    }

    [Fact]
    public async Task Failed_handover_leaves_the_user_active_and_records_unchanged()
    {
        var (tenant, admin, leaver, receiver) = await SetUpAsync();
        var userManager = await factory.CreateUserAsync(await TenantAsync(tenant));
        await factory.CreateRoleAsync(tenant, [CoreModule.Permissions.ManageUsers, "crm.companies.read"], userManager);
        var leaverMembership = await factory.GetMembershipIdAsync(leaver.Id, tenant);
        var receiverMembership = await factory.GetMembershipIdAsync(receiver.Id, tenant);

        using var adminClient = await LoginAsync(admin);
        var company = await CreateAsync(adminClient, "crm.company", new JsonObject { ["name"] = "Kalır", ["ownerId"] = Id(leaver) });

        // Firma düzenleme izni olmayan kullanıcı yöneticisi devredemez; devre dışı bırakma da geri alınır.
        using var client = await LoginAsync(userManager);
        var denied = await client.PostAsJsonAsync($"/api/users/{leaverMembership}/disable", new DisableUserRequest(receiverMembership));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Hiçbir kayıt devredilmedi", await denied.Content.ReadAsStringAsync());
        Assert.Equal(UserStatus.Active, (await adminClient.GetFromJsonAsync<UserDto>($"/api/users/{leaverMembership}"))!.Status);
        Assert.Equal(Id(leaver), await OwnerAsync(adminClient, "crm.company", company));

        // Devralan etkin üye olmalı ve devreden kişi olamaz.
        await factory.DisableMembershipAsync(receiver.Id, tenant);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PostAsJsonAsync($"/api/users/{leaverMembership}/disable", new DisableUserRequest(receiverMembership))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PostAsJsonAsync($"/api/users/{leaverMembership}/transfer-records", new TransferRecordsRequest(leaverMembership))).StatusCode);
        Assert.Equal(UserStatus.Active, (await adminClient.GetFromJsonAsync<UserDto>($"/api/users/{leaverMembership}"))!.Status);
    }

    [Fact]
    public async Task Records_of_an_already_disabled_user_can_be_handed_over_later()
    {
        var (tenant, admin, leaver, receiver) = await SetUpAsync();
        var leaverMembership = await factory.GetMembershipIdAsync(leaver.Id, tenant);
        var receiverMembership = await factory.GetMembershipIdAsync(receiver.Id, tenant);
        using var client = await LoginAsync(admin);
        var company = await CreateAsync(client, "crm.company", new JsonObject { ["name"] = "Sonra devir", ["ownerId"] = Id(leaver) });

        // Devirsiz devre dışı bırakma (gövdesiz istek) eskisi gibi çalışır.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{leaverMembership}/disable", null)).StatusCode);
        Assert.Equal(Id(leaver), await OwnerAsync(client, "crm.company", company));

        var response = await client.PostAsJsonAsync($"/api/users/{leaverMembership}/transfer-records", new TransferRecordsRequest(receiverMembership));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<TransferRecordsResponse>())!.Transferred);
        Assert.Equal(Id(receiver), await OwnerAsync(client, "crm.company", company));
    }

    [Fact]
    public async Task Modules_closed_for_the_tenant_are_not_counted()
    {
        var (tenant, admin, leaver, _) = await SetUpAsync();
        await factory.SetModuleAsync(tenant, "crm", enabled: false);
        using var client = await LoginAsync(admin);

        var owned = await client.GetFromJsonAsync<List<OwnedRecordsDto>>(
            $"/api/users/{await factory.GetMembershipIdAsync(leaver.Id, tenant)}/owned-records");
        Assert.Empty(owned!);
    }

    private async Task<(Guid Tenant, ModulixUser Admin, ModulixUser Leaver, ModulixUser Receiver)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var leaver = await factory.CreateUserAsync(tenant);
        var receiver = await factory.CreateUserAsync(tenant);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        return (tenant.Id, admin, leaver, receiver);
    }

    private async Task<Core.Platform.Tenant> TenantAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string entityKey, JsonObject values)
    {
        var response = await client.PostAsJsonAsync($"/api/records/{entityKey}", new RecordWriteRequest(values));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
    }

    private static async Task<string?> OwnerAsync(HttpClient client, string entityKey, Guid id) =>
        (await client.GetFromJsonAsync<RecordResponse>($"/api/records/{entityKey}/{id}"))!.Values["ownerId"]?.GetValue<string>();

    private static string Id(ModulixUser user) => user.Id.ToString();

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
