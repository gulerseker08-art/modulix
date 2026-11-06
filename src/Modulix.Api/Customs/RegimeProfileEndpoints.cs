using System.Security.Claims;
using Modulix.Api.Platform;
using Modulix.Api.Users;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;
using Modulix.Shared.Customs;

namespace Modulix.Api.Customs;

/// <summary>
/// Rejim profili yönetimi (ADR-0032 K16): platform yöneticisine açıktır (<c>/api/platform/regime-profiles</c>, tenant gerekmez). Akış: profil oluştur →
/// taslak içeriği kaydet (doğrulanır) → yayınla (doğrulandı/doğrulanmadı etiketiyle). Yazma uçları Operasyon rolüne bağlıdır (<see cref="PlatformAccessRules"/>).
/// </summary>
public static class RegimeProfileEndpoints
{
    public static IEndpointRouteBuilder MapRegimeProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/regime-profiles")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        group.MapGet("/", async (IRegimeProfileAdmin profiles, CancellationToken ct) => (await profiles.ListAsync(ct)).Select(ToDto).ToList());
        group.MapGet("/{id:guid}", (Guid id, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await profiles.GetAsync(id, ct)))));
        group.MapPost("/", (CreateRegimeProfileRequest request, ClaimsPrincipal user, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () =>
            {
                var created = await profiles.CreateAsync(PlatformEndpoints.UserId(user), request.Code, request.Name, ct);
                return Results.Created($"/api/platform/regime-profiles/{created.Id}", ToDto(created));
            }));
        group.MapPut("/{id:guid}/draft", (Guid id, RegimeProfileContentDto content, ClaimsPrincipal user, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await profiles.SaveDraftAsync(PlatformEndpoints.UserId(user), id, ToContent(content), ct)))));
        group.MapDelete("/{id:guid}/draft", (Guid id, ClaimsPrincipal user, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () =>
            {
                await profiles.DiscardDraftAsync(PlatformEndpoints.UserId(user), id, ct);
                return Results.NoContent();
            }));
        group.MapPost("/{id:guid}/publish", (Guid id, PublishRegimeProfileRequest request, ClaimsPrincipal user, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await profiles.PublishAsync(PlatformEndpoints.UserId(user), id, request.Verified, request.Note, ct)))));
        group.MapPut("/{id:guid}/active", (Guid id, Modulix.Shared.Sectors.SetActiveRequest request, ClaimsPrincipal user, IRegimeProfileAdmin profiles, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await profiles.SetActiveAsync(PlatformEndpoints.UserId(user), id, request.Active, ct)))));

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RegimeProfileNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (RegimeProfileConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return UserEndpoints.ToProblem(ex)!;
        }
    }

    private static RegimeProfileContent ToContent(RegimeProfileContentDto dto) =>
        new(dto.SchemaVersion, dto.RegimeCode, dto.RegistrationNo is { } r
            ? new RegistrationNoRule(r.Pattern, r.MinLength, r.MaxLength, r.Example,
                Enum.TryParse<RuleEnforcement>(r.Enforcement, ignoreCase: true, out var level) && Enum.IsDefined(level)
                    ? level
                    : throw new ArgumentException("Uygulama düzeyi Warn ya da Block olmalıdır.", nameof(dto)))
            : null);

    private static RegimeProfileContentDto ToDto(RegimeProfileContent c) =>
        new(c.SchemaVersion, c.RegimeCode, c.RegistrationNo is { } r ? new RegistrationNoRuleDto(r.Pattern, r.MinLength, r.MaxLength, r.Example, r.Enforcement.ToString()) : null);

    private static RegimeProfileSummaryDto ToDto(RegimeProfileSummary s) =>
        new(s.Id, s.Code, s.Name, s.IsActive, s.PublishedNumber, s.PublishedVerified, s.HasDraft);

    private static RegimeProfileVersionDto ToDto(RegimeProfileVersionView v) =>
        new(v.Id, v.Number, v.State.ToString(), v.IsVerified, v.VerificationNote, v.CreatedAt, v.PublishedAt, ToDto(v.Content));

    private static RegimeProfileDetailDto ToDto(RegimeProfileDetail d) => new(ToDto(d.Summary), [.. d.Versions.Select(ToDto)]);
}
