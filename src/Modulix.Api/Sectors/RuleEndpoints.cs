using System.Security.Claims;
using System.Text.Json;
using Modulix.Api.Access;
using Modulix.Api.Platform;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.FieldRules;
using Modulix.Core.Modules;
using Modulix.Shared.Sectors;

namespace Modulix.Api.Sectors;

/// <summary>
/// Alan kuralı paketleri ve firma uyarlaması (ADR-0029 K7/K8). Paketler platform yöneticisine açıktır (<c>/api/platform/rule-packages</c>);
/// firma yöneticisi (<c>core.customization.manage</c>) etkin kuralları görür ve kendi uyarlamasını yönetir (<c>/api/rules</c>).
/// Kurallar henüz kayıt boru hattında uygulanmaz; bu uçlar yalnızca tanım, yayın ve birleşimi yönetir.
/// </summary>
public static class RuleEndpoints
{
    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var packages = app.MapGroup("/api/platform/rule-packages")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");
        packages.MapGet("/", async (IRulePackageService service, CancellationToken ct) =>
            (await service.ListAsync(ct)).Select(ToDto).ToList());
        packages.MapPost("/", (CreateRulePackageRequest request, ClaimsPrincipal user, IRulePackageService service, CancellationToken ct) =>
            Run(async () =>
            {
                var package = await service.CreateAsync(UserId(user), request.Code, request.Name, ParseLayer(request.Layer), request.NodeId, ct);
                return Results.Created($"/api/platform/rule-packages/{package.Id}", ToDto(package));
            }));
        packages.MapPut("/{id:guid}/draft", (Guid id, SaveRuleDraftRequest request, ClaimsPrincipal user, IRulePackageService service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SaveDraftAsync(UserId(user), id, ToRules(request.Rules), request.Note, request.ExpectedRevision, ct)))));
        packages.MapDelete("/{id:guid}/draft", (Guid id, ClaimsPrincipal user, IRulePackageService service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.DiscardDraftAsync(UserId(user), id, ct)))));
        packages.MapGet("/{id:guid}/preview", (Guid id, IRulePackageService service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.PreviewPublishAsync(id, ct)))));
        packages.MapPost("/{id:guid}/publish", (Guid id, ClaimsPrincipal user, IRulePackageService service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.PublishAsync(UserId(user), id, ct)))));
        packages.MapPost("/{id:guid}/rollback", (Guid id, RollbackRulePackageRequest request, ClaimsPrincipal user, IRulePackageService service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.RollbackAsync(UserId(user), id, request.ToNumber, ct)))));

        var rules = app.MapGroup("/api/rules")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Rules");
        rules.MapGet("/effective", async (ITenantFieldRules service, CancellationToken ct) =>
        {
            var merged = await service.GetEffectiveAsync(ct);
            return new EffectiveRulesDto(
                [.. merged.Rules.Select(ToDto)],
                [.. merged.Conflicts.Select(c => new RuleIssueDto(c.RuleKey, c.Message))],
                [.. merged.Warnings.Select(w => new RuleIssueDto(w.RuleKey, w.Message))]);
        });
        rules.MapGet("/adaptation", async (ITenantFieldRules service, CancellationToken ct) => ToDto(await service.GetAdaptationAsync(ct)));
        rules.MapPut("/adaptation", (SaveRuleAdaptationRequest request, ITenantFieldRules service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SaveAdaptationAsync(ToRules(request.Rules), request.DisabledRuleKeys, request.ExpectedRevision, ct)))));

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RulePackageNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (RulePackageConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (CorruptRulePackageException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return UserEndpoints.ToProblem(ex)!;
        }
    }

    private static Guid UserId(ClaimsPrincipal user) => PlatformEndpoints.UserId(user);

    private static RuleLayerKind ParseLayer(string value) =>
        Enum.TryParse<RuleLayerKind>(value, ignoreCase: true, out var layer) && layer is RuleLayerKind.ProductStandard or RuleLayerKind.SectorPackage
            ? layer
            : throw new ArgumentException("Geçersiz katman (ProductStandard, SectorPackage).", nameof(value));

    private static List<FieldRule> ToRules(List<FieldRuleDto>? dtos) =>
        [.. (dtos ?? throw new ArgumentException("Kural listesi gerekli.", "rules")).Select(ToRule)];

    private static FieldRule ToRule(FieldRuleDto dto)
    {
        if (!Enum.TryParse<FieldRuleEffect>(dto.Effect, ignoreCase: true, out var effect) || !Enum.IsDefined(effect))
        {
            throw new ArgumentException($"Geçersiz kural etkisi: '{dto.Effect}'.", "effect");
        }

        ConditionNode? condition = null;
        if (dto.Condition is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element)
        {
            try
            {
                condition = JsonSerializer.Deserialize<ConditionNode>(element.GetRawText(), AutomationJson.Options);
            }
            catch (JsonException)
            {
                throw new ArgumentException($"Kural koşulu çözülemedi: '{dto.Key}'.", "condition");
            }
        }

        return new FieldRule(dto.Key, dto.Field, effect, condition) { Message = dto.Message, Value = dto.Value, Locked = dto.Locked };
    }

    private static FieldRuleDto ToDto(FieldRule rule) => new(
        rule.Key,
        rule.Field,
        rule.Effect.ToString(),
        rule.Condition is null ? null : JsonSerializer.SerializeToElement(rule.Condition, AutomationJson.Options),
        rule.Message,
        rule.Value,
        rule.Locked);

    private static RulePackageDto ToDto(RulePackageView p) => new(p.Id, p.Code, p.Name, p.Layer.ToString(), p.NodeId, ToDto(p.Published), ToDto(p.Draft));

    private static RulePackageVersionDto? ToDto(RulePackageVersionView? v) =>
        v is null ? null : new RulePackageVersionDto(v.Number, v.Status.ToString(), [.. v.Rules.Select(ToDto)], v.Note, v.Revision, v.PublishedAt);

    private static RulePublishPreviewDto ToDto(RulePublishPreview p) => new(
        [.. p.Issues.Select(i => new RuleIssueDto(i.Path, i.Message))],
        [.. p.Conflicts.Select(c => new RuleIssueDto(c.RuleKey, c.Message))],
        [.. p.Warnings.Select(w => new RuleIssueDto(w.RuleKey, w.Message))],
        [.. p.Added],
        [.. p.Removed],
        [.. p.Changed],
        p.CanPublish);

    private static TenantRuleAdaptationDto ToDto(TenantRuleAdaptationView v) => new([.. v.Rules.Select(ToDto)], [.. v.DisabledRuleKeys], v.Revision);
}
