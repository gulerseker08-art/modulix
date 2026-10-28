using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Birleştirme uçları (ADR-0013): <c>POST /api/crm/{companies|contacts}/{targetId}/merge[/preview]</c> ve
/// <c>GET /api/crm/{companies|contacts}/merged/{oldId}</c>. Ayrı birleştirme izni gerekir (<c>crm.companies.merge</c>,
/// <c>crm.contacts.merge</c>); kaynak ve hedef çağıranın kapsamında olmalıdır. Hepsi kayıt boru hattından geçer.
/// </summary>
internal static class MergeEndpoints
{
    public static void Map(RouteGroupBuilder crm)
    {
        Map(crm, "companies", CrmModule.Entities.Company, CrmModule.Permissions.CompaniesMerge, CrmModule.Permissions.CompaniesRead);
        Map(crm, "contacts", CrmModule.Entities.Contact, CrmModule.Permissions.ContactsMerge, CrmModule.Permissions.ContactsRead);
    }

    private static void Map(RouteGroupBuilder crm, string path, string entityKey, string mergePermission, string readPermission)
    {
        var group = crm.MapGroup(path);

        group.MapPost("/{targetId:guid}/merge/preview", async (Guid targetId, MergeRecordsRequest body, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.PreviewMergeAsync(entityKey, ToRequest(targetId, body), ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!, targetId)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(mergePermission);

        group.MapPost("/{targetId:guid}/merge", async (Guid targetId, MergeRecordsRequest body, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.MergeAsync(entityKey, ToRequest(targetId, body), ct);
            return result.IsSuccess
                ? Results.Ok(new MergeResultDto(
                    result.Value!.Target.Id,
                    result.Value.Target.Version,
                    result.Value.SourceId,
                    [.. result.Value.Impacts.Select(i => new MergeImpactDto(i.Label, i.Count))]))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(mergePermission);

        // Eski bir kimliğin gittiği kayıt: hedefi göremeyen çağıran için 404 (eski kimliğin varlığı da belli edilmez).
        group.MapGet("/merged/{id:guid}", async (Guid id, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.ResolveMergedAsync(new RecordRef(entityKey, id), ct);
            return result.IsSuccess
                ? Results.Ok(new MergedRecordDto(result.Value!.SourceId, result.Value.TargetId, result.Value.MergedAt))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(readPermission);
    }

    private static MergeRequest ToRequest(Guid targetId, MergeRecordsRequest body) =>
        new(body.SourceId, targetId, body.SourceVersion, body.TargetVersion, body.FieldChoices);

    private static MergePreviewDto ToDto(MergePreview preview, Guid targetId) => new(
        preview.Source.Id,
        targetId,
        preview.Source.Version,
        preview.Target.Version,
        NameOf(preview.Source),
        NameOf(preview.Target),
        [.. preview.Fields.Select(f => new MergeFieldDiffDto(f.Field, f.Source, f.Target, f.Suggested))],
        [.. preview.Impacts.Select(i => new MergeImpactDto(i.Label, i.Count))],
        preview.Blockers);

    private static string NameOf(RecordData record) =>
        record.Values["name"]?.GetValue<string>()
        ?? $"{record.Values["firstName"]?.GetValue<string>()} {record.Values["lastName"]?.GetValue<string>()}".Trim();
}
