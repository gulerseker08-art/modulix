using Modulix.Api.Access;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Teams;
using Modulix.Shared.Users;

namespace Modulix.Api.Users;

/// <summary>Ekipler (Y8). Ekip ataması kullanıcı yönetiminin parçasıdır: <c>core.users.manage</c> ister.</summary>
public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/teams")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("Teams");
        teams.MapGet("/", async (ITeamService service, CancellationToken ct) => Results.Ok((await service.ListAsync(ct)).Select(ToDto).ToList()));
        teams.MapGet("/{teamId:guid}", async (Guid teamId, ITeamService service, CancellationToken ct) =>
            await service.GetAsync(teamId, ct) is { } team ? Results.Ok(ToDto(team)) : TeamNotFound());
        teams.MapPost("/", (SaveTeamRequest request, ITeamService service, CancellationToken ct) => RunAsync(async () =>
        {
            var team = await service.CreateAsync(request.Name, request.Description, ct);
            return Results.Created($"/api/teams/{team.Id}", ToDto(team));
        }));
        teams.MapPut("/{teamId:guid}", (Guid teamId, SaveTeamRequest request, ITeamService service, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateAsync(teamId, request.Name, request.Description, ct) is { } team ? Results.Ok(ToDto(team)) : TeamNotFound()));
        teams.MapDelete("/{teamId:guid}", async (Guid teamId, ITeamService service, CancellationToken ct) =>
            await service.DeleteAsync(teamId, ct) ? Results.NoContent() : TeamNotFound());
        teams.MapPut("/{teamId:guid}/members", (Guid teamId, SetTeamMembersRequest request, ITeamService service, IMembershipScope scope, CancellationToken ct) => RunAsync(async () =>
        {
            // Ekip üyeliği "ekibinin kayıtları" kapsamında kayıt görünürlüğünü etkiler (ADR-0026): yönetici olmayan işlemi yapan,
            // kapsamı dışındaki kullanıcıları ekibe ekleyemez ya da ekipten çıkaramaz.
            var wanted = (request.MembershipIds ?? []).ToHashSet();
            if (await service.GetAsync(teamId, ct) is { } current)
            {
                var changed = wanted.Except(current.Members.Select(m => m.MembershipId))
                    .Concat(current.Members.Select(m => m.MembershipId).Where(id => !wanted.Contains(id)));
                foreach (var id in changed)
                {
                    if (!await scope.CanManageAsync(id, ct))
                    {
                        throw new ArgumentException("Kapsamınız dışındaki kullanıcıların ekip üyeliğini değiştiremezsiniz.", "membershipIds");
                    }
                }
            }

            return await service.SetMembersAsync(teamId, wanted, ct) is { } team ? Results.Ok(ToDto(team)) : TeamNotFound();
        }));

        // Kullanıcı ayrıntısındaki "Ekipler".
        var membership = app.MapGroup("/api/users/{membershipId:guid}/teams")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("Teams");
        // Kapsam dışı üyelik (başka bayinin kullanıcısı) başka tenant'ınki gibi "bulunamadı" döner (ADR-0026).
        membership.MapGet("/", async (Guid membershipId, ITeamService service, IMembershipScope scope, CancellationToken ct) =>
            await scope.CanManageAsync(membershipId, ct) && await service.GetMembershipTeamsAsync(membershipId, ct) is { } list
                ? Results.Ok(list.Select(ToDto).ToList())
                : UserNotFound());
        membership.MapPut("/", (Guid membershipId, SetMembershipTeamsRequest request, ITeamService service, IMembershipScope scope, CancellationToken ct) => RunAsync(async () =>
            await scope.CanManageAsync(membershipId, ct) && await service.SetMembershipTeamsAsync(membershipId, request.TeamIds ?? [], ct)
                ? Results.NoContent()
                : UserNotFound()));

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static TeamDto ToDto(TeamSummary team) => new(team.Id, team.Name, team.Description, team.MemberCount);

    private static TeamDetailDto ToDto(TeamDetail team) => new(
        team.Id,
        team.Name,
        team.Description,
        team.Members.Select(m => new TeamMemberDto(m.MembershipId, m.UserId, m.DisplayName, m.Email)).ToList());

    private static IResult TeamNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ekip bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult UserNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
}
