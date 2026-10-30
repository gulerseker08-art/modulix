using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Customization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0020 sadeleştirme: davet ve sektör profili akışları tek veritabanında tek işlemdir. Çağıranın açtığı işleme
/// katılırlar; işlem geri alınınca hiçbir parça (üyelik, rol ataması, denetim kaydı, profil anahtarı, özel alanlar) kalmaz.
/// </summary>
public class TransactionalFlowsTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Invitation_joins_the_ambient_transaction_and_leaves_nothing_when_rolled_back()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var role = await factory.CreateRoleAsync(tenant.Id, ["crm.companies.read"]);
        var email = $"davet-{Guid.NewGuid():N}@test.modulix";

        Guid membershipId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var result = await scope.ServiceProvider.GetRequiredService<IUserAdministration>()
                .InviteAsync(new UserInvitation(email, "Davetli Kişi", [role.Id], "https://localhost"), CancellationToken.None);
            membershipId = result.User.MembershipId;

            // İşlem içinde her parça yazılmış durumda.
            Assert.Equal(1, await db.Memberships.CountAsync(m => m.Id == membershipId));
            Assert.Equal(1, await db.MembershipRoles.CountAsync(mr => mr.MembershipId == membershipId));

            await transaction.RollbackAsync();
        }

        const string memberships = "SELECT COUNT(*) FROM [platform].[Memberships] WHERE [Id] = @id";
        const string roles = "SELECT COUNT(*) FROM [core].[MembershipRoles] WHERE [MembershipId] = @id";
        const string audit = "SELECT COUNT(*) FROM [core].[AuditLog] WHERE [RecordId] = @id";
        Assert.Equal(0, await factory.ScalarAsync<int>(null, memberships, ("@id", membershipId)));
        Assert.Equal(0, await factory.ScalarAsync<int>(tenant.Id, roles, ("@id", membershipId)));
        Assert.Equal(0, await factory.ScalarAsync<int>(tenant.Id, audit, ("@id", membershipId)));

        // Davette açılan yeni hesap da aynı işlemdedir (Identity aynı bağlamı kullanır): geri alınınca o da kalmaz; yeniden
        // davet hesabı ve üyeliği birlikte oluşturur.
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Users] WHERE [Email] = @e", ("@e", email)));
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var again = await scope.ServiceProvider.GetRequiredService<IUserAdministration>()
                .InviteAsync(new UserInvitation(email, "Davetli Kişi", [role.Id], "https://localhost"), CancellationToken.None);
            Assert.Equal(1, await factory.ScalarAsync<int>(null, memberships, ("@id", again.User.MembershipId)));
            Assert.Equal(1, await factory.ScalarAsync<int>(tenant.Id, roles, ("@id", again.User.MembershipId)));
            Assert.Equal(1, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Users] WHERE [Email] = @e", ("@e", email)));
        }
    }

    [Fact]
    public async Task Sector_profile_is_applied_all_or_nothing()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var plan = await scope.ServiceProvider.GetRequiredService<ISectorProfileService>()
                .ApplyAsync("clinic", enableModules: true, CancellationToken.None);
            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Fields);
            Assert.Equal("clinic", await db.Tenants.Where(t => t.Id == tenant.Id).Select(t => t.ProfileKey).SingleAsync());

            await transaction.RollbackAsync();
        }

        Assert.Null(await factory.ScalarAsync<string>(null, "SELECT [ProfileKey] FROM [platform].[Tenants] WHERE [Id] = @t", ("@t", tenant.Id)));
        Assert.Equal(0, await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM [meta].[CustomFields] WHERE [TenantId] = @t", ("@t", tenant.Id)));

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var plan = await scope.ServiceProvider.GetRequiredService<ISectorProfileService>()
                .ApplyAsync("clinic", enableModules: true, CancellationToken.None);
            Assert.NotNull(plan);
        }

        Assert.Equal("clinic", await factory.ScalarAsync<string>(null, "SELECT [ProfileKey] FROM [platform].[Tenants] WHERE [Id] = @t", ("@t", tenant.Id)));
        Assert.True(await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM [meta].[CustomFields] WHERE [TenantId] = @t", ("@t", tenant.Id)) > 0);
    }
}
