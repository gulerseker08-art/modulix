using Modulix.Api.Access;
using Modulix.Api.Automation;
using Modulix.Api.Sectors;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Automation;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Shared.Customization;
using Modulix.Shared.Sectors;
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
        group.MapGet("/starters", async (ISubProfileService service, CancellationToken ct) =>
            (await service.ListStartersAsync(ct)).Select(s => new SubProfileStarterDto(s.Key, s.Name, s.Description, s.ActivityName, s.OptionLabels, s.FeatureLabels, s.MissingModules, s.Added)).ToList());
        group.MapPost("/starters/{key}", (string key, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
        {
            if (await service.CreateFromStarterAsync(key, ct) is not { } created)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Hazır alt profil bulunamadı.");
            }

            return Results.Created($"/api/customization/sub-profiles/{created.Id}", ToDto(created, modules));
        }));
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
        group.MapPost("/{id:guid}/features", (Guid id, SaveSubProfileFeatureRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.AddFeatureAsync(id, new SubProfileFeatureInput(request.Group ?? string.Empty, request.Value ?? string.Empty), ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapDelete("/{id:guid}/features/{featureId:guid}", (Guid id, Guid featureId, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.RemoveFeatureAsync(id, featureId, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        // Kural editörünün alan listesi: firmanın alan kümesi (özel alanlar dahil). Otomasyon kataloğuyla aynıdır ama okuma izni yerine bu uçtaki
        // özelleştirme izniyle verilir (alt profil kuralı yazan, otomasyon izni olmasa da alanları görebilmeli); eylemler bu uçta gelmez.
        group.MapGet("/rule-catalog", async (IAutomationCatalog catalog, CancellationToken ct) =>
            Results.Ok(AutomationEndpoints.ToDto(await catalog.GetAsync(ct)) with { Actions = [] }));
        group.MapGet("/{id:guid}/rules/preview",(Guid id, ISubProfileService service, CancellationToken ct) => RunAsync(async () =>
            await service.PreviewRulesAsync(id, ct) is { } preview
                ? Results.Ok(new SubProfileRulePreviewDto(
                    [.. preview.Rules.Select(RuleEndpoints.ToDto)],
                    [.. preview.Conflicts.Select(c => new RuleIssueDto(c.RuleKey, c.Message))],
                    [.. preview.Warnings.Select(w => new RuleIssueDto(w.RuleKey, w.Message))],
                    preview.CanActivate))
                : NotFound()));
        group.MapPost("/{id:guid}/rules", (Guid id, SaveSubProfileRuleRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.AddRuleAsync(id, ToInput(request), ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapPut("/{id:guid}/rules/{ruleId:guid}", (Guid id, Guid ruleId, SaveSubProfileRuleRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateRuleAsync(id, ruleId, ToInput(request), ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapDelete("/{id:guid}/rules/{ruleId:guid}", (Guid id, Guid ruleId, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
            await service.RemoveRuleAsync(id, ruleId, ct) is { } view ? Results.Ok(ToDto(view, modules)) : NotFound()));
        group.MapPost("/{id:guid}/options",(Guid id, SaveSubProfileOptionRequest request, ISubProfileService service, ModuleCatalog modules, CancellationToken ct) => RunAsync(async () =>
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
            o.AppliedAt))],
        [.. v.Features.Select(f => new SubProfileFeatureDto(f.Id, f.Group, f.Value))],
        [.. v.Rules.Select(r => new SubProfileRuleDto(r.Id, r.Name, RuleEndpoints.ToDto(r.Rule)))]);

    private static SubProfileRuleInput ToInput(SaveSubProfileRuleRequest request) =>
        new(request.Name ?? string.Empty, RuleEndpoints.ToRule(request.Rule ?? throw new ArgumentException("Kural gerekli.", "rule")));

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alt profil ya da seçenek bulunamadı.");
}
