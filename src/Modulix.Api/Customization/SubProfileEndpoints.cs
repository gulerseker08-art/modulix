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
/// Firma alt profilleri (ADR-0041), firma yöneticisi için: <c>/api/customization/sub-profiles</c>, <c>core.customization.manage</c> ister.
/// Önizleme yolu <c>/preview</c> ile biter ve yazmaz. Kurallar <see cref="ISubProfileService"/>'tedir.
/// </summary>
public static class SubProfileEndpoints
{
    public static IEndpointRouteBuilder MapSubProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customization/sub-profiles")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");

        group.MapGet("/", async (ISubProfileService service, ModuleCatalog modules, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(p => ToDto(p, modules)).ToList());
        group.MapGet("/{id:guid}", (Guid id, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.GetAsync(id, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapPost("/", (SaveSubProfileRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
        {
            var created = await service.CreateAsync(new SubProfileInput(request.Name ?? string.Empty, request.Description, request.ActivityCode), ct);
            return Results.Created($"/api/customization/sub-profiles/{created.Id}", ToDto(created, modules));
        }));
        group.MapPut("/{id:guid}", (Guid id, SaveSubProfileRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateAsync(id, new SubProfileInput(request.Name ?? string.Empty, request.Description, request.ActivityCode), request.Revision, ct) is { } view
                ? Results.Ok(ToDto(view, modules))
                : NotFound()));
        group.MapPost("/{id:guid}/status", (Guid id, SetSubProfileStatusRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
        {
            if (!Enum.TryParse<SubProfileStatus>(request.Status, ignoreCase: false, out var status) || !Enum.IsDefined(status))
            {
                throw new ArgumentException("Geçersiz durum (Draft, Active, Inactive).", "status");
            }

            return await service.SetStatusAsync(id, status, request.Revision, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound();
        }));
        group.MapGet("/{id:guid}/apply/preview", (Guid id, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.PreviewApplyAsync(id, ct) is { } plan ? Results.Ok(SectorProfileEndpoints.ToDto(plan, modules)) : NotFound()));
        group.MapPost("/{id:guid}/apply", (Guid id, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.ApplyPendingAsync(id, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapPost("/{id:guid}/options", (Guid id, SaveSubProfileOptionRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.AddOptionAsync(id, ToInput(request), ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapPut("/{id:guid}/options/{optionId:guid}", (Guid id, Guid optionId, SaveSubProfileOptionRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateOptionAsync(id, optionId, ToInput(request), ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapDelete("/{id:guid}/options/{optionId:guid}", (Guid id, Guid optionId, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.RemoveOptionAsync(id, optionId, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SubProfileNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (SubProfileConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static SubProfileOptionInput ToInput(SaveSubProfileOptionRequest request)
    {
        if (!Enum.TryParse<SubProfileOptionKind>(request.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Geçersiz seçenek türü (Field, ListValue).", "kind");
        }

        return new SubProfileOptionInput(
            kind,
            request.TargetKey ?? string.Empty,
            request.Label ?? string.Empty,
            request.FieldType is { } type ? (CoreFieldType)type : null,
            request.Required,
            request.ShowInList,
            request.Choices,
            request.Category);
    }

    internal static SubProfileDto ToDto(SubProfileView v, ModuleCatalog modules) => new(
        v.Id,
        v.Code,
        v.Name,
        v.Description,
        v.ActivityCode,
        v.ActivityName,
        v.Status.ToString(),
        v.Revision,
        v.OptionCount,
        v.PendingCount,
        v.CreatedAt,
        v.ActivatedAt,
        [.. v.Options.Select(o => new SubProfileOptionDto(
            o.Id,
            o.Kind.ToString(),
            o.TargetKey,
            o.Kind == SubProfileOptionKind.Field
                ? modules.FindCustomizable(o.TargetKey)?.Entity.DisplayName ?? o.TargetKey
                : modules.FindChoiceList(o.TargetKey)?.List.DisplayName ?? o.TargetKey,
            o.Label,
            o.FieldType is { } type ? (FieldType)type : null,
            o.Required,
            o.ShowInList,
            o.Choices,
            o.Category,
            o.AppliedAt))]);

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alt profil ya da seçenek bulunamadı.");
}
