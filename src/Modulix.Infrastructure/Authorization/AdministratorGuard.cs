using Microsoft.EntityFrameworkCore;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// "Tenant'ta en az bir etkin yönetici kalır" kuralı. Yönetici: sistem rolü atanmış, etkin üyeliği olan kişi.
/// Rol ataması bunu kullanır; üyeliği devre dışı bırakma da (Y2) aynı kuralı kullanmalıdır.
///
/// Kullanım: açık bir veritabanı işlemi içinde önce <see cref="LockAsync"/>, sonra
/// <see cref="EnsureAnotherActiveAdministratorAsync"/>, sonra değişiklik ve commit. Kilit sistem rolünün satırını
/// işlem sonuna kadar tutar; aynı anda birbirinin yetkisini kaldırmaya çalışan iki yönetici sırayla işlenir ve
/// ikincisi güncel durumu görür.
/// </summary>
internal sealed class AdministratorGuard(ModulixDbContext db)
{
    public const string LastAdministratorMessage =
        "Kuruluşta en az bir yönetici kalmalı. Önce başka bir kullanıcıya Yönetici rolü verin.";

    public const string AdministratorRequiredMessage =
        "Yönetici rolünü ve yöneticilerin hesaplarını yalnızca bir yönetici değiştirebilir.";

    /// <summary>Aktif tenant'ın sistem rolünü kilitler ve kimliğini döner; tenant'ta sistem rolü yoksa <c>null</c>.</summary>
    public async Task<Guid?> LockAsync(CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Yönetici kilidi açık bir veritabanı işlemi içinde alınmalıdır.");
        }

        var systemRoleId = await db.Roles.AsNoTracking()
            .Where(r => r.IsSystem)
            .Select(r => (Guid?)r.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (systemRoleId is { } id)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM [core].[Roles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}",
                cancellationToken);
        }

        return systemRoleId;
    }

    /// <summary>
    /// İşlemi yapan üyelik yönetici değilse <see cref="UserAdministrationException"/> (yönetici rolüne veya bir
    /// yöneticiye dokunan işlemler). Kilit altında çağrılırsa işlem sürerken yöneticiliği kaldırılamaz.
    /// </summary>
    public async Task EnsureIsAdministratorAsync(Guid systemRoleId, Guid actingMembershipId, CancellationToken cancellationToken)
    {
        if (!await db.MembershipRoles.AnyAsync(mr => mr.MembershipId == actingMembershipId && mr.RoleId == systemRoleId, cancellationToken))
        {
            throw new UserAdministrationException(UserAdministrationError.AdministratorRequired, AdministratorRequiredMessage);
        }
    }

    /// <summary>
    /// Üyelik yönetici olmaktan çıkarsa tenant'ta başka etkin yönetici kalıyor mu? Kalmıyorsa
    /// <see cref="RoleConflictException"/>. Üyelik zaten etkin değilse yönetici sayısı değişmez; denetim gerekmez.
    /// </summary>
    public async Task EnsureAnotherActiveAdministratorAsync(Guid systemRoleId, Guid membershipId, CancellationToken cancellationToken)
    {
        var isActive = await db.Memberships.AnyAsync(
            m => m.Id == membershipId && m.Status == MembershipStatus.Active, cancellationToken);
        if (!isActive)
        {
            return;
        }

        var anotherAdministrator = await (
            from assignment in db.MembershipRoles
            join membership in db.Memberships on assignment.MembershipId equals membership.Id
            where assignment.RoleId == systemRoleId
                  && membership.Id != membershipId
                  && membership.Status == MembershipStatus.Active
            select membership.Id)
            .AnyAsync(cancellationToken);

        if (!anotherAdministrator)
        {
            throw new RoleConflictException(LastAdministratorMessage);
        }
    }
}
