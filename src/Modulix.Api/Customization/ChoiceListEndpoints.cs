using Modulix.Api.Access;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Shared.Customization;

namespace Modulix.Api.Customization;

/// <summary>
/// Seçim listeleri (Y9b). <c>GET /api/choice-lists</c> her üyeye açıktır (formlar, listeler ve huni değerleri buradan çizer);
/// düzenleme <c>/api/customization/lists</c> <c>core.customization.manage</c> ister. Kurallar <see cref="ChoiceListRules"/>'tadır.
/// </summary>
public static class ChoiceListEndpoints
{
    public static IEndpointRouteBuilder MapChoiceListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/choice-lists")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Customization")
            .MapGet("/", async (IChoiceListService service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());

        var lists = app.MapGroup("/api/customization/lists")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");
        lists.MapGet("/", async (IChoiceListService service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());
        lists.MapPut("/{key}", async (string key, SaveChoiceListRequest request, IChoiceListService service, CancellationToken ct) =>
        {
            try
            {
                var values = (request.Values ?? [])
                    .Select(v => new ChoiceValueInput(v.Key, v.Label ?? string.Empty, v.Category, v.Active))
                    .ToList();
                return await service.SaveAsync(key, values, ct) is { } list
                    ? Results.Ok(ToDto(list))
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Liste bulunamadı.");
            }
            catch (ArgumentException ex)
            {
                return UserEndpoints.ToProblem(ex)!;
            }
        });

        return app;
    }

    private static ChoiceListDto ToDto(ChoiceListInfo info) => new(
        info.Definition.Key,
        info.Definition.DisplayName,
        info.Definition.EntityKey,
        info.Definition.Field,
        info.Definition.Required,
        [.. info.Definition.Categories.Select(c => new ChoiceCategoryDto(c.Key, c.DisplayName))],
        [.. info.List.Values.Select(v => new ChoiceValueDto(v.Key, v.Label, v.Category, v.Active, v.BuiltIn))],
        info.List.DefaultValue?.Key,
        info.Customized);
}
