using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 1: departman ağacı, kullanıcının departmanı, kullanıcı listesinde departman ve filtre. Yönetim
/// <c>core.users.manage</c> ister; tenant sınırı sunucuda uygulanır (başka tenant'ın departmanı ve üyeliği "yok" sayılır);
/// değişiklikler denetim kaydına yazılır; veritabanı tenant başına tek kökü ve kapalı olmayan ağacı korur.
/// </summary>
public class BusinessUnitTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Tree_is_created_users_are_moved_between_departments_and_listed_with_a_filter()
    {
        var (tenant, admin, member) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Kök departman (merkez) tenant açılırken vardır; her üyelik ona bağlıdır.
        var tree = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!;
        var root = Assert.Single(tree);
        Assert.Null(root.ParentId);
        Assert.Equal(BusinessUnitKindLabels.Center, root.Kind);
        Assert.Equal(0, root.Depth);
        Assert.Equal(2, root.MemberCount);

        var dealer = await CreateUnitAsync(client, root.Id, "Marmara Bayi", "MRM", BusinessUnitKindLabels.Dealer);
        Assert.Equal(1, dealer.Unit.Depth);
        Assert.Equal($"{root.Name} › Marmara Bayi", dealer.Unit.PathNames);
        Assert.Equal("MRM", dealer.Unit.Code);

        var membership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        var before = await client.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit");
        Assert.Equal(root.Id, before!.Id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(dealer.Unit.Id))).StatusCode);
        var after = await client.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit");
        Assert.Equal(dealer.Unit.Id, after!.Id);
        Assert.Equal(dealer.Unit.PathNames, after.PathNames);

        // Kullanıcı kartı ve listesi departmanı gösterir; liste departmana göre süzülür.
        var card = await client.GetFromJsonAsync<UserDto>($"/api/users/{membership}");
        Assert.Equal("Marmara Bayi", card!.BusinessUnitName);
        var filtered = await client.GetFromJsonAsync<UserPageResponse>($"/api/users?businessUnitId={dealer.Unit.Id}");
        Assert.Equal([membership], filtered!.Items.Select(u => u.MembershipId));
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<UserPageResponse>($"/api/users?businessUnitId={root.Id}"))!.TotalCount);

        var detail = await client.GetFromJsonAsync<BusinessUnitDetailDto>($"/api/business-units/{dealer.Unit.Id}");
        Assert.Equal([membership], detail!.Members.Select(m => m.MembershipId));
        Assert.Equal(1, detail.Unit.MemberCount);

        // Ad, kod ve tür değişir; yeri değişmez.
        var renamed = await client.PutAsJsonAsync($"/api/business-units/{dealer.Unit.Id}",
            new UpdateBusinessUnitRequest("Marmara Bölge Bayi", null, BusinessUnitKindLabels.Other));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedUnit = (await renamed.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit;
        Assert.Equal("Marmara Bölge Bayi", renamedUnit.Name);
        Assert.Null(renamedUnit.Code);
        Assert.Equal(root.Id, renamedUnit.ParentId);
    }

    [Fact]
    public async Task Invitation_joins_the_given_department_or_the_root_by_default()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealer = await CreateUnitAsync(client, root.Id, "Ege Bayi", null, BusinessUnitKindLabels.Dealer);

        var withUnit = await InviteAsync(client, $"davet-{Guid.NewGuid():N}@test.modulix", dealer.Unit.Id);
        Assert.Equal(HttpStatusCode.Created, withUnit.StatusCode);
        var invited = (await withUnit.Content.ReadFromJsonAsync<InviteUserResponse>())!.User;
        Assert.Equal("Ege Bayi", invited.BusinessUnitName);

        var withoutUnit = await InviteAsync(client, $"davet-{Guid.NewGuid():N}@test.modulix", null);
        Assert.Equal(HttpStatusCode.Created, withoutUnit.StatusCode);
        Assert.Equal(root.Name, (await withoutUnit.Content.ReadFromJsonAsync<InviteUserResponse>())!.User.BusinessUnitName);

        // Olmayan ya da başka tenant'ın departmanı reddedilir; kullanıcı da oluşmaz.
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        var otherRoot = (await otherClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single();
        var rejected = await InviteAsync(client, $"davet-{Guid.NewGuid():N}@test.modulix", otherRoot.Id);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("businessUnitId", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(4, (await client.GetFromJsonAsync<UserPageResponse>("/api/users"))!.TotalCount);
    }

    [Fact]
    public async Task Tree_rules_are_enforced_with_validation_and_conflict_responses()
    {
        var (tenant, admin, member) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealer = await CreateUnitAsync(client, root.Id, "Karadeniz Bayi", null, BusinessUnitKindLabels.Dealer);

        // Doğrulama (400): ad zorunlu, tür geçerli olmalı, merkez türü yalnızca kökündür.
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(client, root.Id, " ", BusinessUnitKindLabels.Dealer)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(client, root.Id, "Tür yok", "Bilinmeyen")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(client, root.Id, "İkinci merkez", BusinessUnitKindLabels.Center)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(client, root.Id, new string('a', BusinessUnit.NameMaxLength + 1), BusinessUnitKindLabels.Dealer)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/business-units/{root.Id}",
            new UpdateBusinessUnitRequest(root.Name, null, BusinessUnitKindLabels.Dealer))).StatusCode);

        // Çakışma (409): aynı üst departmanda aynı ad (büyük/küçük harf ve boşluk fark etmez), olmayan üst departman 404.
        Assert.Equal(HttpStatusCode.Conflict, (await Create(client, root.Id, " karadeniz bayi ", BusinessUnitKindLabels.Dealer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Create(client, Guid.NewGuid(), "Yetim", BusinessUnitKindLabels.Dealer)).StatusCode);
        var sibling = await CreateUnitAsync(client, root.Id, "Doğu Bayi", null, BusinessUnitKindLabels.Dealer);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/business-units/{sibling.Unit.Id}",
            new UpdateBusinessUnitRequest("Karadeniz Bayi", null, BusinessUnitKindLabels.Dealer))).StatusCode);

        // Ağaç en fazla dört seviyedir (kök dahil): 0 → 1 → 2 → 3, dördüncü alt seviye reddedilir.
        var level2 = await CreateUnitAsync(client, dealer.Unit.Id, "İl", null, BusinessUnitKindLabels.Other);
        var level3 = await CreateUnitAsync(client, level2.Unit.Id, "İlçe", null, BusinessUnitKindLabels.Other);
        Assert.Equal(3, level3.Unit.Depth);
        Assert.Equal(HttpStatusCode.Conflict, (await Create(client, level3.Unit.Id, "Mahalle", BusinessUnitKindLabels.Other)).StatusCode);

        // Kök kapatılamaz; kullanıcısı ya da etkin alt departmanı olan departman kapatılamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await SetActive(client, root.Id, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetActive(client, dealer.Unit.Id, false)).StatusCode);
        var membership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(level3.Unit.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetActive(client, level3.Unit.Id, false)).StatusCode);

        // Kullanıcıyı geri taşıyınca alttan yukarı kapatılır; kapalı departmana kullanıcı atanamaz ve altına departman eklenemez.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(root.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(client, level3.Unit.Id, false)).StatusCode);
        var rejectedMove = await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(level3.Unit.Id));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedMove.StatusCode);
        Assert.Contains("businessUnitId", await rejectedMove.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Create(client, level3.Unit.Id, "Kapalıya ekle", BusinessUnitKindLabels.Other)).StatusCode);

        // Yeniden açma: üst departman kapalıyken açılamaz.
        Assert.Equal(HttpStatusCode.OK, (await SetActive(client, level2.Unit.Id, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetActive(client, level3.Unit.Id, true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(client, level2.Unit.Id, true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(client, level3.Unit.Id, true)).StatusCode);

        // Olmayan departman 404; olmayan kullanıcı 404; olmayan departmana atama 400.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/business-units/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}/business-unit", new SetMembershipBusinessUnitRequest(root.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task One_tenant_cannot_see_or_use_the_departments_and_memberships_of_another()
    {
        var (tenant, admin, member) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealer = await CreateUnitAsync(client, root.Id, "Gizli Bayi", null, BusinessUnitKindLabels.Dealer);

        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        var otherMember = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        var otherMembership = await factory.GetMembershipIdAsync(otherMember.Id, other.Id);
        var memberMembership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);

        // Liste ve ayrıntı yalnızca kendi tenant'ının departmanını gösterir; başkasınınki "yok" sayılır.
        var otherTree = (await otherClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!;
        var otherRoot = Assert.Single(otherTree);
        Assert.DoesNotContain(otherTree, u => u.Id == dealer.Unit.Id || u.Id == root.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/business-units/{dealer.Unit.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync($"/api/business-units/{dealer.Unit.Id}",
            new UpdateBusinessUnitRequest("Ele geçir", null, BusinessUnitKindLabels.Dealer))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetActive(otherClient, dealer.Unit.Id, false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Create(otherClient, dealer.Unit.Id, "Yabancı alt", BusinessUnitKindLabels.Dealer)).StatusCode);

        // Başkasının üyeliği ya da departmanı kullanılamaz (varlığı belli edilmez).
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/users/{memberMembership}/business-unit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync(
            $"/api/users/{memberMembership}/business-unit", new SetMembershipBusinessUnitRequest(otherRoot.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await otherClient.PutAsJsonAsync(
            $"/api/users/{otherMembership}/business-unit", new SetMembershipBusinessUnitRequest(dealer.Unit.Id))).StatusCode);
        Assert.Equal(otherRoot.Id, (await otherClient.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{otherMembership}/business-unit"))!.Id);

        // Kullanıcı listesindeki departman süzgeci başka tenant'ın departmanıyla sızdırmaz.
        Assert.Equal(0, (await otherClient.GetFromJsonAsync<UserPageResponse>($"/api/users?businessUnitId={dealer.Unit.Id}"))!.TotalCount);

        // Veritabanı katmanı: oturum tenant'ı olmadan satır görünmez, kendi tenant'ında görünür (RLS).
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [core].[BusinessUnits]"));
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [core].[BusinessUnitMembers]"));
        Assert.Equal(2, await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM [core].[BusinessUnits]"));
        Assert.Equal(1, await factory.ScalarAsync<int>(other.Id, "SELECT COUNT(*) FROM [core].[BusinessUnits]"));
    }

    [Fact]
    public async Task Only_user_managers_can_read_or_change_the_departments()
    {
        var (tenant, admin, member) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var root = (await adminClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealer = await CreateUnitAsync(adminClient, root.Id, "Akdeniz Bayi", null, BusinessUnitKindLabels.Dealer);
        var membership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);

        // Yönetici olmayan, kullanıcı yönetimi izni olmayan kişi hiçbir şeyi okuyamaz ya da değiştiremez.
        using var memberClient = await LoginAsync(member);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/business-units")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync($"/api/business-units/{dealer.Unit.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create(memberClient, root.Id, "Yetkisiz", BusinessUnitKindLabels.Dealer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync($"/api/business-units/{dealer.Unit.Id}",
            new UpdateBusinessUnitRequest("Yetkisiz", null, BusinessUnitKindLabels.Dealer))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetActive(memberClient, dealer.Unit.Id, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync($"/api/users/{membership}/business-unit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync(
            $"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(dealer.Unit.Id))).StatusCode);

        // Hiçbiri etki etmedi.
        var tree = (await adminClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!;
        Assert.Equal(2, tree.Count);
        Assert.Equal("Akdeniz Bayi", tree.Single(u => u.Id == dealer.Unit.Id).Name);
        Assert.Equal(root.Id, (await adminClient.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{membership}/business-unit"))!.Id);

        // Kimlik doğrulaması olmadan da erişilemez.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/business-units")).StatusCode);
    }

    [Fact]
    public async Task Changes_are_audited_and_the_database_keeps_a_single_root_per_tenant()
    {
        var (tenant, admin, member) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var dealer = await CreateUnitAsync(client, root.Id, "Trakya Bayi", null, BusinessUnitKindLabels.Dealer);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/business-units/{dealer.Unit.Id}",
            new UpdateBusinessUnitRequest("Trakya Bölge Bayi", "TRK", BusinessUnitKindLabels.Dealer))).StatusCode);
        var membership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(dealer.Unit.Id))).StatusCode);
        // Aynı departmana yeniden atama değişiklik değildir; ikinci denetim kaydı yazılmaz.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(dealer.Unit.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/users/{membership}/business-unit", new SetMembershipBusinessUnitRequest(root.Id))).StatusCode);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var unitActions = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.BusinessUnit && a.RecordId == dealer.Unit.Id)
                .OrderBy(a => a.OccurredAt)
                .Select(a => a.Action)
                .ToListAsync();
            Assert.Equal([AuditAction.Created, AuditAction.Updated], unitActions);

            var moves = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.Membership && a.RecordId == membership)
                .ToListAsync();
            Assert.Equal(2, moves.Count(a => a.Action == AuditAction.Updated));

            // Tenant başına ikinci kök (veritabanı kısıtı) ve aynı üst departmanda aynı ad kaydedilemez.
            db.BusinessUnits.Add(BusinessUnit.CreateRoot("İkinci kök"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var parent = await db.BusinessUnits.SingleAsync(u => u.Id == root.Id);
            db.BusinessUnits.Add(BusinessUnit.CreateChild(parent, "Trakya Bölge Bayi", null, BusinessUnitKinds.Dealer));
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<SqlException>(failure.InnerException);
        }

        // Her üyelik tam bir departmandadır: ikinci departman üyeliği kaydedilemez.
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            db.BusinessUnitMembers.Add(new BusinessUnitMember(dealer.Unit.Id, membership));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Member)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return (tenant, admin, member);
    }

    private static async Task<BusinessUnitDetailDto> CreateUnitAsync(HttpClient client, Guid parentId, string name, string? code, string kind)
    {
        var response = await Create(client, parentId, name, kind, code);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!;
    }

    private static Task<HttpResponseMessage> Create(HttpClient client, Guid parentId, string name, string kind, string? code = null) =>
        client.PostAsJsonAsync("/api/business-units", new CreateBusinessUnitRequest(parentId, name, code, kind));

    private static Task<HttpResponseMessage> SetActive(HttpClient client, Guid id, bool isActive) =>
        client.PutAsJsonAsync($"/api/business-units/{id}/active", new SetBusinessUnitActiveRequest(isActive));

    private static Task<HttpResponseMessage> InviteAsync(HttpClient client, string email, Guid? businessUnitId) =>
        client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Davetli Kişi", [], businessUnitId));

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
