using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Shared.Customization;

namespace Modulix.Api.Customization;

/// <summary>
/// Form düzenleri (ADR-0021). <c>GET /api/form-layouts</c> her üyeye açıktır (formlar buradan çizilir); düzenleme
/// <c>/api/customization/form-layouts/{kayıtTürü}</c> <c>core.customization.manage</c> ister. Kurallar
/// <see cref="FormLayoutRules"/>'tadır; tenant yalnızca oturumdan gelir.
/// </summary>
public static class FormLayoutEndpoints
{
    public static IEndpointRouteBuilder MapFormLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/form-layouts")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Customization")
            .MapGet("/", async (IFormLayoutService service, CancellationToken ct) =>
                (await service.ListAsync(ct)).Select(ToDto).ToList());

        var admin = app.MapGroup("/api/customization/form-layouts")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");

        admin.MapGet("/{entityKey}", async (string entityKey, IFormLayoutService service, CancellationToken ct) =>
            await service.FindAsync(entityKey, ct) is { } layout ? Results.Ok(ToDto(layout)) : Results.NotFound());

        admin.MapPut("/{entityKey}", async (string entityKey, SaveFormLayoutRequest request, IFormLayoutService service, CancellationToken ct) =>
        {
            var input = new FormLayoutInput(
                [.. (request.Tabs ?? []).Select(t => new FormTabLayout(t.Key, t.Label))],
                [.. (request.Sections ?? []).Select(s => new FormSectionLayout(s.Key, s.Label, s.Icon, s.Tab))],
                [.. (request.Fields ?? []).Select(f => new FormFieldInput(f.Key, f.Section, f.Span, f.Required, f.Visible))]);
            var result = await service.SaveAsync(entityKey, input, request.Version, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        });

        admin.MapDelete("/{entityKey}", async (string entityKey, long version, IFormLayoutService service, CancellationToken ct) =>
        {
            var result = await service.ResetAsync(entityKey, version, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        });

        return app;
    }

    internal static FormLayoutDto ToDto(FormLayoutInfo info) => new(
        info.EntityKey,
        info.DisplayName,
        [.. info.Tabs.Select(t => new FormTabDto(t.Key, t.Label))],
        [.. info.Sections.Select(s => new FormSectionDto(s.Key, s.Label, s.Icon, s.Tab))],
        [.. info.Fields.Select(f => new FormFieldDto(f.Key, f.Label, f.Section, f.Span, f.Required, f.Visible, f.Fixed, f.IsCustom, f.DefaultRequired))],
        info.Customized,
        info.Version);
}
