using Modulix.Api.Access;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Shared.Customization;

namespace Modulix.Api.Customization;

/// <summary>
/// Sektör profilleri (Y9c), firma yöneticisi için: <c>/api/customization/profiles</c>, <c>core.customization.manage</c> ister.
/// Firma yöneticisi modül açamaz; kapalı modülün alanları ve değerleri eklenmez (önizlemede ayrıca gösterilir). Platform
/// yöneticisinin uçları <c>/api/platform</c> altındadır.
/// </summary>
public static class SectorProfileEndpoints
{
    public static IEndpointRouteBuilder MapSectorProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/api/customization/profiles")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");
        profiles.MapGet("/", async (ISectorProfileService service, SectorProfileCatalog catalog, CancellationToken ct) =>
        {
            var current = await service.GetAsync(ct);
            return new SectorProfilesResponse(current.ProfileKey, current.AppliedAt, ToDto(catalog));
        });
        profiles.MapGet("/{key}/preview", async (string key, ISectorProfileService service, ModuleCatalog modules, CancellationToken ct) =>
            await service.PreviewAsync(key, enableModules: false, ct) is { } plan ? Results.Ok(ToDto(plan, modules)) : NotFound());
        profiles.MapPost("/{key}/apply", async (string key, ISectorProfileService service, ModuleCatalog modules, CancellationToken ct) =>
        {
            try
            {
                return await service.ApplyAsync(key, enableModules: false, ct) is { } plan ? Results.Ok(ToDto(plan, modules)) : NotFound();
            }
            catch (ArgumentException ex)
            {
                return UserEndpoints.ToProblem(ex)!;
            }
        });

        return app;
    }

    internal static List<SectorProfileDto> ToDto(SectorProfileCatalog catalog) =>
        [.. catalog.Profiles.Select(p => new SectorProfileDto(p.Key, p.Name, p.Description, p.Modules))];

    internal static ProfilePlanDto ToDto(ProfilePlan plan, ModuleCatalog modules) => new(
        plan.Profile.Key,
        plan.Profile.Name,
        [.. plan.ModulesToEnable.Select(m => modules.Find(m)?.DisplayName ?? m)],
        [.. plan.UnavailableModules.Select(m => modules.Find(m)?.DisplayName ?? m)],
        [.. plan.Fields.Select(f => new ProfileFieldDto(
            modules.FindCustomizable(f.EntityKey)?.Entity.DisplayName ?? f.EntityKey, f.Label, (Shared.Customization.CustomFieldType)f.Type, f.Options ?? []))],
        [.. plan.ListValues.Select(v => new ProfileListValueDto(modules.FindChoiceList(v.ListKey)?.List.DisplayName ?? v.ListKey, v.Label))],
        plan.IsEmpty);

    internal static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Sektör profili bulunamadı.");
}
