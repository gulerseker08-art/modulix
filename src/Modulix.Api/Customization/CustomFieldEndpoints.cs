using Modulix.Api.Access;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Shared.Customization;
using CoreFieldType = Modulix.Core.Customization.CustomFieldType;
using FieldType = Modulix.Shared.Customization.CustomFieldType;

namespace Modulix.Api.Customization;

/// <summary>
/// Özel alanlar (Y9a). <c>GET /api/custom-fields</c> her üyeye açıktır (formlar ve listeler alanları buradan çizer);
/// tanım ekranı <c>/api/customization/fields</c> <c>core.customization.manage</c> ister. Kurallar
/// <see cref="ICustomFieldService"/>'tedir.
/// </summary>
public static class CustomFieldEndpoints
{
    public static IEndpointRouteBuilder MapCustomFieldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/custom-fields")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Customization")
            .MapGet("/", async (ICustomFieldService service, CancellationToken ct) =>
                (await service.ListAsync(null, includeArchived: false, ct)).Select(ToDto).ToList());

        var fields = app.MapGroup("/api/customization")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");
        fields.MapGet("/entities", async (ICustomFieldService service, CancellationToken ct) =>
            (await service.ListEntitiesAsync(ct)).Select(e => new CustomizableEntityDto(e.EntityKey, e.DisplayName)).ToList());
        fields.MapGet("/fields", async (string? entityKey, ICustomFieldService service, CancellationToken ct) =>
            (await service.ListAsync(entityKey, includeArchived: true, ct)).Select(ToDto).ToList());
        fields.MapPost("/fields", (SaveCustomFieldRequest request, ICustomFieldService service, CancellationToken ct) => RunAsync(async () =>
        {
            var field = await service.CreateAsync(request.EntityKey, ToInput(request), ct);
            return Results.Created($"/api/customization/fields/{field.Id}", ToDto(field));
        }));
        fields.MapPut("/fields/{id:guid}", (Guid id, SaveCustomFieldRequest request, ICustomFieldService service, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateAsync(id, ToInput(request), ct) is { } field ? Results.Ok(ToDto(field)) : NotFound()));
        fields.MapPost("/fields/{id:guid}/archive", (Guid id, ICustomFieldService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetArchivedAsync(id, archived: true, ct) is { } field ? Results.Ok(ToDto(field)) : NotFound()));
        fields.MapPost("/fields/{id:guid}/restore", (Guid id, ICustomFieldService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetArchivedAsync(id, archived: false, ct) is { } field ? Results.Ok(ToDto(field)) : NotFound()));
        fields.MapPut("/fields/order", (ReorderCustomFieldsRequest request, ICustomFieldService service, CancellationToken ct) => RunAsync(async () =>
        {
            await service.ReorderAsync(request.EntityKey, request.Ids ?? [], ct);
            return Results.NoContent();
        }));

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

    private static CustomFieldInput ToInput(SaveCustomFieldRequest request) => new(
        request.Label ?? string.Empty,
        (CoreFieldType)request.Type,
        request.Required,
        request.ShowInList,
        request.Options?.Select(o => new CustomFieldOptionInput(o.Key, o.Label ?? string.Empty, o.Active)).ToList());

    internal static CustomFieldDto ToDto(CustomFieldInfo field) => new(
        field.Id,
        field.EntityKey,
        field.Key,
        field.Label,
        (FieldType)field.Type,
        field.Required,
        field.ShowInList,
        field.Archived,
        field.Options.Select(o => new CustomFieldOptionDto(o.Key, o.Label, o.Active)).ToList());

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alan bulunamadı.");
}
