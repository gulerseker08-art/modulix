using System.Security.Claims;
using Modulix.Core.Platform;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Platform ekibi (ADR-0028 Dilim 5): /api/platform/team. Yalnızca Owner rolü (kural tablosu <see cref="PlatformAccessRules"/>).
/// Hesap silinmez; rolü boşaltmak yalnızca platform erişimini kapatır. Owner ataması komut satırında kalır.
/// </summary>
public static class PlatformTeamEndpoints
{
    public static IEndpointRouteBuilder MapPlatformTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var team = app.MapGroup("/api/platform/team")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        team.MapGet("", async (IPlatformTeam service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());

        team.MapPost("", async (AddPlatformMemberRequest request, ClaimsPrincipal user, IPlatformTeam service, CancellationToken ct) =>
            await RunAsync(async () => Results.Ok(ToDto(await service.SetRolesByEmailAsync(
                request.Email, Parse(request.Roles), PlatformEndpoints.UserId(user), ct)))));

        team.MapPut("/{userId:guid}", async (Guid userId, SetPlatformRolesRequest request, ClaimsPrincipal user, IPlatformTeam service, CancellationToken ct) =>
            await RunAsync(async () => Results.Ok(ToDto(await service.SetRolesAsync(userId, Parse(request.Roles), PlatformEndpoints.UserId(user), ct)))));

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (PlatformEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Rol adları büyük/küçük harfe duyarsız; bilinmeyen ad <see cref="ArgumentException"/> (alan <c>roles</c>).</summary>
    private static PlatformRole Parse(IReadOnlyList<string>? names)
    {
        var roles = PlatformRole.None;
        foreach (var name in names ?? [])
        {
            if (!Enum.TryParse<PlatformRole>(name?.Trim(), ignoreCase: true, out var role) || role == PlatformRole.None || !Enum.IsDefined(role))
            {
                throw new ArgumentException($"Bilinmeyen rol: '{name}'.", "roles");
            }

            roles |= role;
        }

        return roles;
    }

    private static PlatformTeamMemberDto ToDto(PlatformTeamMember member) => new(
        member.UserId, member.DisplayName, member.Email, [.. PlatformRoles.Split(member.Roles).Select(r => r.ToString())], member.TwoFactorEnabled);
}
