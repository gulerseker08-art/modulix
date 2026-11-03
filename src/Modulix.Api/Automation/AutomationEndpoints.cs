using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using Modulix.Shared.Automation;

namespace Modulix.Api.Automation;

/// <summary>
/// Otomasyon kuralları (ADR-0027, Dilim 1). Okuma <c>core.automation.read</c>, taslak ve önizleme <c>core.automation.manage</c>,
/// yayın ve açma/kapama <c>core.automation.publish</c> ister. Tenant oturumdan belirlenir; başka tenant'ın kuralı "bulunamadı"
/// döner. Kurallar bu sürümde çalışmaz; önizleme hiçbir yan etki üretmez.
/// </summary>
public static class AutomationEndpoints
{
    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/automation").RequireAuthorization().WithTags("Automation");

        group.MapGet("/catalog", async (IAutomationCatalog catalog, CancellationToken ct) =>
            Results.Ok(ToDto(await catalog.GetAsync(ct))))
            .RequirePermission(CoreModule.Permissions.ReadAutomation);

        group.MapGet("/rules", async (
            string? moduleKey, string? status, string? search, int? page, int? pageSize, IAutomationRuleService service, CancellationToken ct) =>
        {
            AutomationRuleStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<AutomationRuleStatus>(status, ignoreCase: true, out var value))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["status"] = ["Durum Draft, Active ya da Inactive olmalı."] },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                parsed = value;
            }

            var result = await service.ListAsync(new AutomationRuleQuery(moduleKey, parsed, search, page ?? 1, pageSize ?? 50), ct);
            return Results.Ok(new AutomationRulePageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        }).RequirePermission(CoreModule.Permissions.ReadAutomation);

        group.MapGet("/rules/{ruleId:guid}", async (Guid ruleId, IAutomationRuleService service, CancellationToken ct) =>
            await service.GetAsync(ruleId, ct) is { } detail ? Results.Ok(ToDto(detail)) : RuleNotFound())
            .RequirePermission(CoreModule.Permissions.ReadAutomation);

        group.MapPost("/rules", (CreateAutomationRuleRequest request, IAutomationRuleService service, CancellationToken ct) => RunAsync(async () =>
        {
            var content = ToContent(request.Name, request.Description, request.Priority, request.StopProcessing, request.ActiveFrom, request.ActiveUntil, request.Definition);
            var detail = await service.CreateAsync(request.ModuleKey, request.EntityKey, content, ct);
            return Results.Created($"/api/automation/rules/{detail.Rule.Id}", ToDto(detail));
        })).RequirePermission(CoreModule.Permissions.ManageAutomation);

        group.MapPut("/rules/{ruleId:guid}", (Guid ruleId, UpdateAutomationRuleRequest request, IAutomationRuleService service, CancellationToken ct) => RunAsync(async () =>
        {
            var content = ToContent(request.Name, request.Description, request.Priority, request.StopProcessing, request.ActiveFrom, request.ActiveUntil, request.Definition);
            return await service.UpdateDraftAsync(ruleId, content, request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : RuleNotFound();
        })).RequirePermission(CoreModule.Permissions.ManageAutomation);

        group.MapDelete("/rules/{ruleId:guid}/draft", (Guid ruleId, IAutomationRuleService service, CancellationToken ct) => RunAsync(async () =>
            await service.DiscardDraftAsync(ruleId, ct) ? Results.NoContent() : RuleNotFound()))
            .RequirePermission(CoreModule.Permissions.ManageAutomation);

        group.MapPost("/rules/{ruleId:guid}/publish", (Guid ruleId, PublishAutomationRuleRequest request, IAutomationRuleService service, CancellationToken ct) => RunAsync(async () =>
            await service.PublishAsync(ruleId, request.Activate, request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : RuleNotFound()))
            .RequirePermission(CoreModule.Permissions.PublishAutomation);

        group.MapPut("/rules/{ruleId:guid}/active", (Guid ruleId, SetAutomationRuleActiveRequest request, IAutomationRuleService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetActiveAsync(ruleId, request.IsActive, ct) is { } detail ? Results.Ok(ToDto(detail)) : RuleNotFound()))
            .RequirePermission(CoreModule.Permissions.PublishAutomation);

        group.MapPost("/preview", (PreviewAutomationRequest request, IAutomationPreview preview, CancellationToken ct) => RunAsync(async () =>
        {
            var definition = AutomationJson.Deserialize(request.Definition);
            var changes = request.Changes is { ValueKind: JsonValueKind.Object } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : null;
            var result = await preview.PreviewAsync(new AutomationPreviewRequest(request.ModuleKey, request.EntityKey, definition, request.RecordId, changes), ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : ToProblem(result.Error!);
        })).RequirePermission(CoreModule.Permissions.ManageAutomation);

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AutomationPublishException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: ex.Message,
                extensions: new Dictionary<string, object?> { ["issues"] = ex.Issues.Select(i => new AutomationIssueDto(i.Path, i.Message)).ToList() });
        }
        catch (AutomationRuleConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (Exception ex) when (ex is AutomationRuleException or FormatException)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["definition"] = [ex.Message] },
                title: "Lütfen kural bilgilerini düzeltin.",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult RuleNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kural bulunamadı.");

    private static IResult ToProblem(RecordError error) => error.Code switch
    {
        RecordErrorCode.NotFound or RecordErrorCode.UnknownEntity => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: error.Message),
        RecordErrorCode.PermissionDenied or RecordErrorCode.FeatureNotLicensed => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: error.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message),
    };

    private static AutomationVersionContent ToContent(
        string name, string? description, int priority, bool stopProcessing, DateTimeOffset? from, DateTimeOffset? until, JsonElement definition) =>
        new(name, description, priority, stopProcessing, from, until, AutomationJson.Deserialize(definition));

    private static AutomationRuleSummaryDto ToDto(AutomationRuleSummary rule) => new(
        rule.Id, rule.Name, rule.ModuleKey, rule.EntityKey, rule.Status.ToString(), rule.PublishedVersionNo, rule.HasDraft,
        rule.Priority, rule.UpdatedAt, rule.Version);

    private static AutomationVersionDto ToDto(AutomationVersionInfo version) => new(
        version.Id,
        version.VersionNo,
        version.State.ToString(),
        version.Content.Name,
        version.Content.Description,
        version.Content.Priority,
        version.Content.StopProcessing,
        version.Content.ActiveFrom,
        version.Content.ActiveUntil,
        JsonSerializer.SerializeToElement(version.Content.Definition, AutomationJson.Options),
        version.Hash,
        version.Summary,
        version.PublishedAt,
        version.PublishedBy);

    private static AutomationRuleDetailDto ToDto(AutomationRuleDetail detail) => new(
        ToDto(detail.Rule),
        detail.Draft is null ? null : ToDto(detail.Draft),
        detail.Published is null ? null : ToDto(detail.Published),
        [.. detail.DraftIssues.Select(i => new AutomationIssueDto(i.Path, i.Message))],
        [.. detail.PublishedIssues.Select(i => new AutomationIssueDto(i.Path, i.Message))]);

    private static AutomationPreviewDto ToDto(AutomationPreview preview) => new(
        [.. preview.Issues.Select(i => new AutomationIssueDto(i.Path, i.Message))],
        preview.TriggerText,
        preview.ConditionText,
        preview.Evaluated,
        preview.ConditionsPassed,
        preview.Explanation is null ? null : ToDto(preview.Explanation),
        [.. preview.PlannedActions.Select(a => new PlannedActionDto(
            a.StepId, a.Action, a.DisplayName, a.SideEffect.ToString(), JsonSerializer.SerializeToElement(a.Parameters), a.Permitted, a.Note))],
        preview.Warnings);

    private static ConditionExplanationDto ToDto(ConditionExplanation explanation) =>
        new(explanation.Passed, explanation.Text, [.. explanation.Children.Select(ToDto)]);

    private static AutomationFieldDto ToDto(AutomationFieldDefinition field) => new(
        field.Key,
        field.Label,
        field.Type.ToString(),
        [.. AutomationOperators.For(field.Type).Select(o => o.ToString())],
        [.. field.Options.Select(o => new AutomationOptionDto(o.Key, o.Label))],
        field.LookupKey,
        field.Hint);

    private static AutomationCatalogDto ToDto(AutomationCatalogView catalog) => new(
        [.. catalog.Entities.Select(e => new AutomationEntityDto(
            e.ModuleKey,
            e.EntityKey,
            e.DisplayName,
            [.. e.Fields.Select(ToDto)],
            [.. e.Events.Select(ev => new AutomationEventDto(ev.Key, ev.EventType, ev.DisplayName))],
            [.. e.Relations.Select(r => new AutomationRelationDto(r.Key, r.Label, r.TargetEntityKey, [.. r.TargetFields.Select(ToDto)]))]))],
        [.. catalog.Actions.Select(a => new AutomationActionDto(
            a.Definition.Key,
            a.ModuleKey,
            a.Definition.DisplayName,
            a.Definition.Description,
            a.Definition.SideEffect.ToString(),
            a.Definition.SupportsDryRun,
            a.Available,
            a.UnavailableReason,
            [.. a.Definition.Parameters.Select(p => new AutomationParameterDto(
                p.Name, p.Label, p.Type.ToString(), p.Required, p.Description, [.. p.Options.Select(o => new AutomationOptionDto(o.Key, o.Label))], p.LookupKey))]))]);
}
