namespace Modulix.Shared.Users;

// Ekipler (/api/teams, Y8). Ekip, "ekibinin kayıtları" kapsamındaki kişinin hangi kayıtları göreceğini belirler.

public sealed record TeamDto(Guid Id, string Name, string? Description, int MemberCount);

public sealed record TeamMemberDto(Guid MembershipId, Guid UserId, string DisplayName, string Email);

public sealed record TeamDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<TeamMemberDto> Members);

public sealed record SaveTeamRequest(string Name, string? Description);

public sealed record SetTeamMembersRequest(IReadOnlyList<Guid> MembershipIds);

public sealed record SetMembershipTeamsRequest(IReadOnlyList<Guid> TeamIds);
