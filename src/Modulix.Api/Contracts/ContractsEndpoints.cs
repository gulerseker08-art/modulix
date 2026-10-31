using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Settings;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Contracts;

namespace Modulix.Api.Contracts;

/// <summary>
/// Sözleşme uç noktaları (/api/contracts/...; ADR-0022). Başlık, taraf ve kalem CRUD'u kayıt boru hattından geçer; durum, sürüm ve
/// toplam alanları genel uçlardan yazılamaz. Durum geçişleri ayrı eylem uç noktalarıdır (<c>POST .../contracts/{id}/{eylem}</c>) ve tek
/// kural kümesini (<see cref="IContractWorkflow"/>) kullanır. Eylem izinleri serviste, kayıt bulunduktan SONRA denetlenir: kapsam dışı kayıt
/// 404, izinsiz eylem 403 alır. Eylemler kayıt sürümünü ister (eşzamanlı çakışmada yalnızca biri kazanır, diğeri 409 alır).
/// </summary>
public static class ContractsEndpoints
{
    private const string Prefix = "/api/contracts";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static IEndpointRouteBuilder MapContractsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .RequireAuthorization()
            .RequireModule(ContractsModule.ModuleKey)
            .WithTags("Sözleşmeler");

        ResourceEndpoints.MapResource<ContractDto, SaveContractRequest>(group, Prefix, "contracts", ContractsModule.Entities.Contract,
            ContractsModule.Permissions.ContractsRead, ContractsModule.Permissions.ContractsWrite);
        ResourceEndpoints.MapResource<ContractPartyDto, SaveContractPartyRequest>(group, Prefix, "contract-parties", ContractsModule.Entities.ContractParty,
            ContractsModule.Permissions.ContractsRead, ContractsModule.Permissions.ContractsWrite);
        ResourceEndpoints.MapResource<ContractLineDto, SaveContractLineRequest>(group, Prefix, "contract-lines", ContractsModule.Entities.ContractLine,
            ContractsModule.Permissions.ContractsRead, ContractsModule.Permissions.ContractsWrite);

        var contracts = group.MapGroup("/contracts");
        contracts.MapGet("/{id:guid}/history", async (Guid id, IContractWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.HistoryAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(h => new ContractHistoryDto(h.Id, h.Kind, h.At, h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapGet("/{id:guid}/actions", async (Guid id, IContractWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new ContractActionDto(a.Action, a.Allowed, a.Code, a.Message, a.RequiresReason)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        // Eylem izinleri serviste denetlenir; uç nokta yalnızca okuma iznini ister ki kayıt yoksa 404 dönsün.
        foreach (var action in ContractAction.All)
        {
            contracts.MapPost($"/{{id:guid}}/{action}", async (Guid id, ContractTransitionRequest r, IContractWorkflow w, CancellationToken ct) =>
            {
                var result = await w.RunAsync(id, action, r.Version, r.Reason, ct);
                return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<ContractDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        }

        // Kabul edilmiş tekliften sözleşme (dilim 3): önizleme (kalemler ve sözleşmede olanlar) ve oluşturma. İzinler serviste denetlenir (yazma + finansal).
        var fromQuote = group.MapGroup("/from-quote");
        fromQuote.MapGet("/{quoteId:guid}", async (Guid quoteId, IContractFromQuote service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(quoteId, ct);
            return result.IsSuccess
                ? Results.Ok(JsonSerializer.Deserialize<QuoteSourceDto>(JsonSerializer.Serialize(result.Value, Json), Json))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        fromQuote.MapPost("/{quoteId:guid}", async (Guid quoteId, CreateContractFromQuoteRequest r, IContractFromQuote service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(quoteId,
                new ContractFromQuoteOptions(r.Type, r.StartDate, r.TermMonths, r.RenewalMode, r.RenewalPeriodMonths, r.NoticePeriodDays, r.LineKeys, r.OwnerId), ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/contracts/{result.Value!.Id}", ResourceEndpoints.ToDto<ContractDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        // Belgeler (dilim 4): liste, önizleme, üretim ve indirme. İzinler serviste denetlenir (sözleşme görünürlüğü önce: kapsam dışı 404).
        contracts.MapGet("/{id:guid}/documents", async (Guid id, IContractDocuments documents, CancellationToken ct) =>
        {
            var result = await documents.ListAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        contracts.MapPost("/{id:guid}/documents/preview", async (Guid id, PreviewContractDocumentRequest r, IContractDocuments documents, CancellationToken ct) =>
        {
            var result = await documents.PreviewAsync(id, r.TemplateId, r.UseDraft, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        contracts.MapPost("/{id:guid}/documents", async (Guid id, GenerateContractDocumentRequest r, IContractDocuments documents, CancellationToken ct) =>
        {
            var result = await documents.GenerateAsync(id, r.TemplateId, ct);
            return result.IsSuccess ? Results.Created($"{Prefix}/contracts/{id}/documents/{result.Value!.Id}", ToDto(result.Value)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        contracts.MapGet("/{id:guid}/documents/{documentId:guid}/file", async (Guid id, Guid documentId, IContractDocuments documents, HttpContext http, CancellationToken ct) =>
        {
            var result = await documents.GetFileAsync(id, documentId, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(result.Value!.Content, "application/pdf", result.Value.Info.FileName);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        // Şablonlar (dilim 4): CRUD kayıt boru hattından (düzenleme izni), yaşam döngüsü eylemleri ayrı izinle (yayımlama).
        ResourceEndpoints.MapResource<ContractTemplateDto, SaveContractTemplateRequest>(group, Prefix, "templates", ContractsModule.Entities.ContractTemplate,
            ContractsModule.Permissions.ContractsRead, ContractsModule.Permissions.TemplatesEdit);
        var templates = group.MapGroup("/templates");
        templates.MapGet("/variables", () => Results.Ok(ContractTemplateVariables.All.Select(v => new TemplateVariableDto(v.Key, v.Label, v.Group, v.Financial, v.IsBlock)).ToList()))
            .RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapGet("/sample", () => Results.Ok(new TemplateSampleDto(ContractTemplateSamples.ServiceName, ContractTemplateSamples.ServiceBody.Replace("\r\n", "\n"), ContractTemplateSamples.Notice)))
            .RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapPost("/{id:guid}/publish", async (Guid id, TemplateActionRequest r, IContractTemplates service, CancellationToken ct) =>
            TemplateResult(await service.PublishAsync(id, r.Version, r.Reason, ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapPost("/{id:guid}/archive", async (Guid id, TemplateActionRequest r, IContractTemplates service, CancellationToken ct) =>
            TemplateResult(await service.ArchiveAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapPost("/{id:guid}/restore", async (Guid id, TemplateActionRequest r, IContractTemplates service, CancellationToken ct) =>
            TemplateResult(await service.RestoreAsync(id, r.Version, ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapGet("/{id:guid}/versions", async (Guid id, IContractTemplates service, CancellationToken ct) =>
        {
            var result = await service.VersionsAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
        templates.MapGet("/{id:guid}/versions/{versionNo}", async (Guid id, string versionNo, IContractTemplates service, CancellationToken ct) =>
        {
            if (!int.TryParse(versionNo, out var no)) { return Results.NotFound(); }
            var result = await service.VersionAsync(id, no, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        // İç onay (dilim 5): sözleşmenin onay planı/ilerlemesi, onay iş listesi, kurallar ve vekâletler. Karar verme eylem uç noktalarıyla yapılır (approve, reject-approval, return-for-changes).
        contracts.MapGet("/{id:guid}/approval", async (Guid id, IContractApprovals approvals, CancellationToken ct) =>
        {
            var result = await approvals.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        group.MapGet("/approvals/inbox", async (bool? mine, IContractApprovals approvals, CancellationToken ct) =>
        {
            var result = await approvals.InboxAsync(mine ?? true, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(i => new ContractApprovalInboxItemDto(
                    i.ContractId, i.Number, i.Title, i.Type, i.VersionNo, i.OwnerId, i.SubmittedBy, i.SubmittedAt, i.DueAt, i.IsOverdue, i.CurrentStep, i.StepCount,
                    i.WaitingFor, i.CanDecide, i.ActingAsDelegate, i.CannotDecideReason, i.Rules)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsApprove);

        ResourceEndpoints.MapResource<ContractApprovalRuleDto, SaveContractApprovalRuleRequest>(group, Prefix, "approval-rules", ContractsModule.Entities.ContractApprovalRule,
            ContractsModule.Permissions.ApprovalsManage, ContractsModule.Permissions.ApprovalsManage);
        ResourceEndpoints.MapResource<ContractApprovalDelegationDto, SaveContractApprovalDelegationRequest>(group, Prefix, "approval-delegations", ContractsModule.Entities.ContractApprovalDelegation,
            ContractsModule.Permissions.ApprovalsManage, ContractsModule.Permissions.ApprovalsManage);

        contracts.MapGet("/{id:guid}/versions", async (Guid id, IContractWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.VersionsAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapGet("/{id:guid}/versions/{versionNo}", async (Guid id, string versionNo, IContractWorkflow workflow, CancellationToken ct) =>
        {
            // Sürüm numarası kısıtsız okunur ki oturumsuz istek rota eşleşmemesiyle 404 değil 401 alsın; geçersiz numara 404'tür.
            if (!int.TryParse(versionNo, out var no)) { return Results.NotFound(); }
            var result = await workflow.VersionAsync(id, no, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        // Karşılaştırma: ?with=0 güncel taslak.
        contracts.MapGet("/{id:guid}/versions/{versionNo}/compare", async (Guid id, string versionNo, int? with, IContractWorkflow workflow, CancellationToken ct) =>
        {
            if (!int.TryParse(versionNo, out var no)) { return Results.NotFound(); }
            var result = await workflow.CompareAsync(id, no, with ?? 0, ct);
            return result.IsSuccess
                ? Results.Ok(new ContractCompareDto(
                    [.. result.Value!.Header.Select(f => new FieldChangeDto(f.Field, f.Before, f.After))],
                    [.. result.Value.Lines.Select(l => new LineChangeDto(l.StableKey, l.Change, l.Name, [.. l.Fields.Select(f => new FieldChangeDto(f.Field, f.Before, f.After))]))],
                    [.. result.Value.Parties.Select(p => new PartyChangeDto(p.Id, p.Change, p.Role, [.. p.Fields.Select(f => new FieldChangeDto(f.Field, f.Before, f.After))]))]))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        return app;
    }

    private static IResult TemplateResult(Modulix.Core.Contracts.Records.RecordResult<Modulix.Core.Contracts.Records.RecordData> result) =>
        result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<ContractTemplateDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static ContractApprovalDto ToDto(ContractApprovalView v) => new(
        v.Status, v.VersionNo,
        [.. v.Rules.Select(r => new ContractApprovalMatchDto(r.RuleId, r.RuleVersion, r.Name, r.Step, r.ApproverId, r.Reason))],
        v.Note,
        [.. v.Steps.Select(s => new ContractApprovalStepDto(s.Step, s.Approvers, s.Completed, s.ApprovedBy, s.IsCurrent))],
        [.. v.Decisions.Select(d => new ContractApprovalDecisionDto(d.Id, d.VersionNo, d.StepNo, d.ActorId, d.OnBehalfOf, d.Decision, d.Reason, d.At))],
        v.CurrentStep, v.CanDecide, v.ActingAsDelegate, v.CannotDecideReason, v.SubmittedBy, v.SubmittedAt, v.DueAt, v.IsOverdue);

    private static ContractDocumentDto ToDto(ContractDocumentInfo d) =>
        new(d.Id, d.ContractVersionNo, d.IsFinal, d.TemplateId, d.TemplateVersionNo, d.TemplateName, d.IsExample, d.FileName, d.SizeBytes, d.Sha256, d.CreatedBy, d.CreatedAt);

    private static ContractTemplateVersionDto ToDto(ContractTemplateVersionView v) =>
        new(v.Id, v.VersionNo, v.Name, v.Sha256, v.IsExample, v.Notes, v.PublishedBy, v.PublishedAt, v.Body);

    private static ContractPreviewDto ToDto(ContractPreview p) =>
        new(p.TemplateName, p.TemplateVersionNo, p.IsExample, p.IsDraftTemplate, p.IsFinal, [.. p.Blocks.Select(ToDto)], p.Missing);

    private static RenderedBlockDto ToDto(DocBlock block) => block switch
    {
        HeadingBlock h => new("Heading", h.Level, h.Text, null, null, null, null),
        ParagraphBlock p => new("Paragraph", 0, p.Text, null, null, null, null),
        TableBlock t => new("Table", 0, null, t.Header, t.Rows, t.Totals, null),
        PartyListBlock l => new("PartyList", 0, null, null, null, l.Items, null),
        SignatureBlock s => new("Signature", 0, null, null, null, null, [.. s.Slots.Select(x => new SignatureSlotDto(x.Title, x.Name))]),
        _ => new("PageBreak", 0, null, null, null, null, null),
    };

    /// <summary>Anlık görüntü aynı alan adlarıyla taşınır (JSON üzerinden tipli DTO'ya).</summary>
    private static ContractVersionDto ToDto(ContractVersionView v) =>
        new(v.Id, v.VersionNo, v.Kind, v.Status, v.Sha256, v.Reason, v.CreatedBy, v.FrozenAt,
            v.Snapshot is null ? null : JsonSerializer.Deserialize<ContractSnapshotDto>(JsonSerializer.Serialize(v.Snapshot, Json), Json));
}

/// <summary>Sözleşme politikası: tenant ayarlarından okunur (modül Core'un ayar servisini görmez; host bu köprüyü sağlar).</summary>
internal sealed class ContractPolicyProvider(IServiceProvider services) : IContractPolicyProvider
{
    public async Task<ContractPolicySettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<ITenantSettings>();
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new ContractPolicySettings(
            string.Equals(await settings.GetValueAsync(ContractsModule.SettingKeys.AllowSelfApproval, cancellationToken), "true", StringComparison.OrdinalIgnoreCase),
            Text(await settings.GetValueAsync(ContractsModule.SettingKeys.ProviderName, cancellationToken)),
            Text(await settings.GetValueAsync(ContractsModule.SettingKeys.ProviderDetails, cancellationToken)),
            Text(await settings.GetValueAsync(ContractsModule.SettingKeys.FooterNote, cancellationToken)),
            Text(await settings.GetValueAsync(Modulix.Core.Modules.CoreModule.SettingKeys.TimeZone, cancellationToken)) ?? "Europe/Istanbul",
            int.TryParse(await settings.GetValueAsync(ContractsModule.SettingKeys.ApprovalDueHours, cancellationToken), out var dueHours) ? Math.Clamp(dueHours, 0, 9999) : 72);
    }
}
