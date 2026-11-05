using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Platform;

internal sealed class PlatformTeam(ModulixDbContext db, UserManager<ModulixUser> users) : IPlatformTeam
{
    public async Task<IReadOnlyList<PlatformTeamMember>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Users.AsNoTracking()
            .Where(u => u.IsPlatformAdministrator)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.PlatformRoles, u.TwoFactorEnabled })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(u => new PlatformTeamMember(
            u.Id, u.DisplayName, u.Email ?? string.Empty, PlatformRoles.Effective(true, u.PlatformRoles), u.TwoFactorEnabled))];
    }

    public async Task<PlatformTeamMember> SetRolesByEmailAsync(string email, PlatformRole roles, Guid actingUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("E-posta zorunludur.", "email");
        }

        var user = await users.FindByEmailAsync(email.Trim())
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Bu e-postayla kayıtlı hesap yok; kişi önce bir firmaya davet edilmiş olmalı.");
        return await ApplyAsync(user, roles, actingUserId, cancellationToken);
    }

    public async Task<PlatformTeamMember> SetRolesAsync(Guid userId, PlatformRole roles, Guid actingUserId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString())
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Hesap bulunamadı.");
        return await ApplyAsync(user, roles, actingUserId, cancellationToken);
    }

    private async Task<PlatformTeamMember> ApplyAsync(ModulixUser user, PlatformRole roles, Guid actingUserId, CancellationToken cancellationToken)
    {
        if ((roles & ~PlatformRoles.Assignable) != PlatformRole.None)
        {
            throw new ArgumentException("Yalnızca Destek, Operasyon ve Finans rolleri atanabilir; platform sahibi komut satırıyla atanır.", "roles");
        }

        if (user.Id == actingUserId)
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Kendi rolünüzü değiştiremezsiniz.");
        }

        var before = PlatformRoles.Effective(user.IsPlatformAdministrator, user.PlatformRoles);
        if (before.HasFlag(PlatformRole.Owner))
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Platform sahibinin rolü arayüzden değiştirilemez.");
        }

        // Rol boşsa erişim kapanır; hesap, firma üyelikleri ve verisi olduğu gibi kalır.
        user.IsPlatformAdministrator = roles != PlatformRole.None;
        user.PlatformRoles = roles;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        db.PlatformAuditLog.Add(new PlatformAuditEntry(
            actingUserId,
            PlatformAuditActions.TeamChanged,
            null,
            null,
            new JsonObject { ["userId"] = user.Id, ["before"] = before.ToString(), ["after"] = roles.ToString() }.ToJsonString(),
            Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);
        return new PlatformTeamMember(user.Id, user.DisplayName, user.Email ?? string.Empty, roles, user.TwoFactorEnabled);
    }
}
