using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 4 (K7): kullanıcının departmanını değiştirmek. Kayıt görünürlüğü sahibin güncel departmanına göre hesaplandığı için
/// değişiklik önce etkisini gösterir; kullanıcının kayıtları varsa kayıtlar kullanıcıyla taşınır ya da eski departmanda kalan bir
/// üyeye devredilir, gerekçe zorunludur ve denetim kaydına yazılır. Devre dışı üyelik de aynı kurallara tabidir. Yönetici olmayan
/// işlemi yapan yalnızca kendi kapsamındaki departmanlar arasında taşıyabilir.
/// </summary>
public class BusinessUnitChangeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CompanyAccess = [CompaniesRead, CompaniesWrite];

    [Fact]
    public async Task Records_follow_the_user_after_an_explicit_decision_and_a_reason()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        using var u1 = await LoginAsync(t.U1);
        await CreateCompanyAsync(u1, "Birinci");
        await CreateCompanyAsync(u1, "İkinci");

        // Önizleme: kayıtlar sayılır, karar ve gerekçe gerekir, kayıtların görünürlüğü uyarısı verilir.
        var preview = await PreviewAsync(admin, t.U1, t.Tenant, t.UnitB.Id);
        Assert.True(preview.IsChange);
        Assert.Equal(2, preview.OwnedTotal);
        Assert.True(preview.DecisionRequired);
        Assert.True(preview.ReasonRequired);
        Assert.Contains(preview.Warnings, w => w.Contains("2 kayıt"));
        Assert.Contains("Bayi A", preview.CurrentPath);
        Assert.Contains("Bayi B", preview.TargetPath);

        // Karar yoksa 409; gerekçe yoksa alan hatası; ikisi de varsa taşınır.
        var membership = await MembershipAsync(t.U1, t.Tenant);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
        var noReason = await Put(admin, membership, new SetMembershipBusinessUnitRequest(t.UnitB.Id, null, MoveRecordsWithUser: true));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("reason", await noReason.Content.ReadAsStringAsync());
        Assert.Equal("Bayi A", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit"))!.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(
            admin, membership, new SetMembershipBusinessUnitRequest(t.UnitB.Id, "bayi-degisikligi-talebi-17", MoveRecordsWithUser: true))).StatusCode);
        Assert.Equal("Bayi B", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit"))!.Name);

        // Kayıtlar yeni departmanla görünür; eski departmanın iş arkadaşı artık görmez.
        using var b1 = await LoginAsync(t.B1);
        using var a2 = await LoginAsync(t.A2);
        Assert.Equal(2, (await b1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!.TotalCount);
        Assert.Equal(0, (await a2.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!.TotalCount);

        // Gerekçe ve kayıtların akıbeti denetim kaydında.
        using var scope = factory.OpenScope(t.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var entries = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.Membership && a.RecordId == membership && a.Action == AuditAction.Updated)
            .ToListAsync();
        Assert.Contains(entries, a => a.Changes.ToString().Contains("bayi-degisikligi-talebi-17") && a.Changes.ToString().Contains("businessUnitRecords"));
    }

    [Fact]
    public async Task A_user_without_records_moves_without_a_decision_or_reason()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var membership = await MembershipAsync(t.A2, t.Tenant);

        var preview = await PreviewAsync(admin, t.A2, t.Tenant, t.UnitB.Id);
        Assert.True(preview.IsChange);
        Assert.Equal(0, preview.OwnedTotal);
        Assert.False(preview.DecisionRequired);
        Assert.False(preview.ReasonRequired);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);

        // Aynı departmana "taşıma" değişiklik değildir.
        var same = await PreviewAsync(admin, t.A2, t.Tenant, t.UnitB.Id);
        Assert.False(same.IsChange);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
    }

    [Fact]
    public async Task Records_can_stay_in_the_old_department_by_transferring_them_to_a_member_who_stays()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        using var u1 = await LoginAsync(t.U1);
        await CreateCompanyAsync(u1, "Kalan 1");
        await CreateCompanyAsync(u1, "Kalan 2");
        var membership = await MembershipAsync(t.U1, t.Tenant);
        var staying = await MembershipAsync(t.A2, t.Tenant);

        // Devralan: kendisi olamaz, eski departmanda ve etkin olmalı; devir ile taşıma birlikte seçilemez.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(
            t.UnitB.Id, "Devir", TransferRecordsToMembershipId: membership))).StatusCode);
        var otherUnit = await Put(admin, membership, new SetMembershipBusinessUnitRequest(
            t.UnitB.Id, "Devir", TransferRecordsToMembershipId: await MembershipAsync(t.B1, t.Tenant)));
        Assert.Equal(HttpStatusCode.BadRequest, otherUnit.StatusCode);
        Assert.Contains("transferRecordsToMembershipId", await otherUnit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(
            t.UnitB.Id, "Devir", MoveRecordsWithUser: true, TransferRecordsToMembershipId: staying))).StatusCode);

        // Başarısız denemeler hiçbir şeyi değiştirmedi: kullanıcı eski departmanında, kayıtlar kendisinde.
        Assert.Equal("Bayi A", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit"))!.Name);
        Assert.Equal(2, (await u1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!.TotalCount);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, membership, new SetMembershipBusinessUnitRequest(
            t.UnitB.Id, "Kayıtlar kalsın", TransferRecordsToMembershipId: staying))).StatusCode);

        // Kayıtlar eski departmanda, devralanda kaldı; yeni departman göremez.
        using var a2 = await LoginAsync(t.A2);
        using var b1 = await LoginAsync(t.B1);
        var seenByA2 = (await a2.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!;
        Assert.Equal(2, seenByA2.TotalCount);
        Assert.All(seenByA2.Items, c => Assert.Equal(t.A2.Id, c.OwnerId));
        Assert.Equal(0, (await b1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!.TotalCount);
        Assert.Equal("Bayi B", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit"))!.Name);
    }

    [Fact]
    public async Task Disabled_members_follow_the_same_rules_and_a_unit_with_them_cannot_be_closed()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var holding = await CreateUnitAsync(admin, t.Root.Id, "Eski çalışanlar");
        var formerNoRecords = await factory.CreateUserAsync(t.Tenant);
        var formerMembership = await MembershipAsync(formerNoRecords, t.Tenant);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, formerMembership, new SetMembershipBusinessUnitRequest(holding.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/users/{formerMembership}/disable", null)).StatusCode);

        // Departmanı kapatmak devre dışı üyeyi de görür.
        var blocked = await admin.PutAsJsonAsync($"/api/business-units/{holding.Id}/active", new SetBusinessUnitActiveRequest(false));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("devre dışı olanlar dahil", await blocked.Content.ReadAsStringAsync());

        // Kaydı yoksa da devre dışı üyeyi taşımak gerekçe ister; önizleme durumu bildirir.
        var preview = await PreviewAsync(admin, formerNoRecords, t.Tenant, t.Root.Id);
        Assert.Equal(UserStatus.Disabled, preview.Status);
        Assert.False(preview.DecisionRequired);
        Assert.True(preview.ReasonRequired);
        Assert.Contains(preview.Warnings, w => w.Contains("devre dışı"));
        var noReason = await Put(admin, formerMembership, new SetMembershipBusinessUnitRequest(t.Root.Id));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Contains("reason", await noReason.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Put(admin, formerMembership, new SetMembershipBusinessUnitRequest(t.Root.Id, "Ayrıldı, arşiv"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/business-units/{holding.Id}/active", new SetBusinessUnitActiveRequest(false))).StatusCode);
    }

    [Fact]
    public async Task A_department_manager_can_only_move_users_within_their_own_scope()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var child = await CreateUnitAsync(admin, t.UnitA.Id, "Bayi A Alt");
        using var unitManager = await LoginAsync(t.Manager);
        using var treeManager = await LoginAsync(t.TreeManager);
        var a2 = await MembershipAsync(t.A2, t.Tenant);
        var b1 = await MembershipAsync(t.B1, t.Tenant);

        // Yalnızca kendi departmanı: başka departmana, başka departmandaki kullanıcıya ve alt departmana yetkisi yok.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(unitManager, a2, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(unitManager, b1, new SetMembershipBusinessUnitRequest(t.UnitA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(unitManager, a2, new SetMembershipBusinessUnitRequest(child.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await unitManager.GetAsync($"/api/users/{a2}/business-unit/change-preview?businessUnitId={t.UnitB.Id}")).StatusCode);

        // "Departman ve altı": kendi ağacı içinde taşıyabilir, dışına taşıyamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(treeManager, a2, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
        // Yönetici her kayıt türünü göremediği için sayım eksik olabilir: kayıt kararı ve gerekçe istenir (güvenli varsayılan).
        var limited = await treeManager.GetFromJsonAsync<BusinessUnitChangePreviewDto>($"/api/users/{a2}/business-unit/change-preview?businessUnitId={child.Id}");
        Assert.True(limited!.OwnedUnknown);
        Assert.True(limited.DecisionRequired);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(treeManager, a2, new SetMembershipBusinessUnitRequest(child.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(treeManager, a2, new SetMembershipBusinessUnitRequest(child.Id, "alt departmana alindi", MoveRecordsWithUser: true))).StatusCode);
        Assert.Equal("Bayi A Alt", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{a2}/business-unit"))!.Name);

        // Hiçbiri başka departmandaki kullanıcıyı değiştirmedi.
        Assert.Equal("Bayi B", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{b1}/business-unit"))!.Name);
    }

    [Fact]
    public async Task Preview_lists_the_department_roles_the_user_loses_and_gains()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var extra = await CreateRoleAsync(admin, "Yalnız B rolü", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Unit);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/business-units/{t.UnitB.Id}/roles",
            new SetBusinessUnitRolesRequest([new BusinessUnitRoleItem(t.DealerRole.Id), new BusinessUnitRoleItem(extra.Id)]))).StatusCode);

        // A → B: ortak rol kalır, B'ye özel rol gelir. A → merkez: bayi rolü kalkar.
        var toB = await PreviewAsync(admin, t.A2, t.Tenant, t.UnitB.Id);
        Assert.Empty(toB.LostRoles);
        Assert.Equal(new[] { "Yalnız B rolü" }, toB.GainedRoles);
        var toRoot = await PreviewAsync(admin, t.A2, t.Tenant, t.Root.Id);
        Assert.Equal(new[] { "Bayi" }, toRoot.LostRoles);
        Assert.Empty(toRoot.GainedRoles);
    }

    [Fact]
    public async Task One_tenant_cannot_preview_or_move_the_users_of_another()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        var otherRoot = (await otherClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single();
        var u1 = await MembershipAsync(t.U1, t.Tenant);

        // Başkasının üyeliği "bulunamadı"; başkasının departmanı geçersiz.
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"/api/users/{u1}/business-unit/change-preview?businessUnitId={otherRoot.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put(otherClient, u1, new SetMembershipBusinessUnitRequest(otherRoot.Id, "x", MoveRecordsWithUser: true))).StatusCode);
        var foreignUnit = await admin.GetAsync($"/api/users/{u1}/business-unit/change-preview?businessUnitId={otherRoot.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, foreignUnit.StatusCode);
        Assert.Contains("businessUnitId", await foreignUnit.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, u1, new SetMembershipBusinessUnitRequest(otherRoot.Id, "x", MoveRecordsWithUser: true))).StatusCode);
        Assert.Equal("Bayi A", (await admin.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{u1}/business-unit"))!.Name);
    }

    [Fact]
    public async Task Unauthorized_users_cannot_preview_or_change_a_department()
    {
        var t = await SetUpAsync();
        var plain = await factory.CreateUserAsync(t.Tenant);
        using var plainClient = await LoginAsync(plain);
        var u1 = await MembershipAsync(t.U1, t.Tenant);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await plainClient.GetAsync($"/api/users/{u1}/business-unit/change-preview?businessUnitId={t.UnitB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(plainClient, u1, new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
    }

    private sealed record Setup(
        Core.Platform.Tenant Tenant,
        ModulixUser Admin,
        ModulixUser U1,
        ModulixUser A2,
        ModulixUser B1,
        ModulixUser Manager,
        ModulixUser TreeManager,
        BusinessUnitDto Root,
        BusinessUnitDto UnitA,
        BusinessUnitDto UnitB,
        RoleDto DealerRole);

    // Bayi A: u1, a2, yönetici (yalnızca departman), yönetici (departman ve altı). Bayi B: b1. "Bayi" rolü iki bayiye atanmıştır.
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var u1 = await factory.CreateUserAsync(tenant);
        var a2 = await factory.CreateUserAsync(tenant);
        var b1 = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var treeManager = await factory.CreateUserAsync(tenant);

        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var unitA = await CreateUnitAsync(client, root.Id, "Bayi A");
        var unitB = await CreateUnitAsync(client, root.Id, "Bayi B");
        var dealer = await CreateRoleAsync(client, "Bayi", CompanyAccess, SharedScope.Unit, RoleAssignLevel.Unit);
        foreach (var unit in new[] { unitA, unitB })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
                $"/api/business-units/{unit.Id}/roles", new SetBusinessUnitRolesRequest([new BusinessUnitRoleItem(dealer.Id)]))).StatusCode);
        }

        foreach (var (user, unit) in new[] { (u1, unitA), (a2, unitA), (manager, unitA), (treeManager, unitA), (b1, unitB) })
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await Put(client, await MembershipAsync(user, tenant), new SetMembershipBusinessUnitRequest(unit.Id))).StatusCode);
        }

        // Yöneticiler kullanıcı yönetir; biri yalnızca kendi departmanını, diğeri alt departmanlarını da görür.
        var unitManagerRole = await CreateRoleAsync(client, "Dep yöneticisi", [ManageUsers, CompaniesRead], SharedScope.Unit, RoleAssignLevel.User);
        var treeManagerRole = await CreateRoleAsync(client, "Ağaç yöneticisi", [ManageUsers, CompaniesRead], SharedScope.UnitAndBelow, RoleAssignLevel.User);
        await SetRolesAsync(client, manager, tenant, unitManagerRole.Id);
        await SetRolesAsync(client, treeManager, tenant, treeManagerRole.Id);
        return new Setup(tenant, admin, u1, a2, b1, manager, treeManager, root, unitA, unitB, dealer);
    }

    private async Task<Guid> MembershipAsync(ModulixUser user, Core.Platform.Tenant tenant) =>
        await factory.GetMembershipIdAsync(user.Id, tenant.Id);

    private async Task<BusinessUnitChangePreviewDto> PreviewAsync(HttpClient client, ModulixUser user, Core.Platform.Tenant tenant, Guid unitId)
    {
        var response = await client.GetAsync($"/api/users/{await MembershipAsync(user, tenant)}/business-unit/change-preview?businessUnitId={unitId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitChangePreviewDto>())!;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, Guid membershipId, SetMembershipBusinessUnitRequest request) =>
        client.PutAsJsonAsync($"/api/users/{membershipId}/business-unit", request);

    private async Task SetRolesAsync(HttpClient admin, ModulixUser user, Core.Platform.Tenant tenant, params Guid[] roleIds)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/memberships/{await MembershipAsync(user, tenant)}/roles", new SetMembershipRolesRequest(roleIds));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BusinessUnitDto> CreateUnitAsync(HttpClient client, Guid parentId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(parentId, name, null, BusinessUnitKindLabels.Dealer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit;
    }

    private static async Task<RoleDto> CreateRoleAsync(
        HttpClient client, string name, string[] permissions, SharedScope scope, RoleAssignLevel level)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(name, permissions, scope, level));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
