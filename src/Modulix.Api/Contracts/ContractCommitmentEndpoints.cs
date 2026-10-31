using System.Globalization;
using System.Text.Json;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Contracts;

namespace Modulix.Api.Contracts;

/// <summary>
/// Taahhüt uç noktaları (/api/contracts/commitments...; ADR-0022, dilim 7). Tanım CRUD'u kayıt boru hattından geçer (yazma <c>contracts.commitments.write</c>);
/// gerçekleşme tamamlama (<c>.complete</c>), muafiyet (<c>.waive</c>), iptal ve değerlendirme turu <see cref="IContractCommitments"/> üzerindendir. İzinler serviste,
/// kayıt bulunduktan sonra denetlenir (kapsam dışı 404, izinsiz 403). Ceza ya da finansal sonuç uygulanmaz. Kanıt dosyası base64 gelir; indirme her zaman ek olarak sunulur.
/// </summary>
internal static class ContractCommitmentEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void MapInternal(RouteGroupBuilder group, string prefix)
    {
        ResourceEndpoints.MapResource<ContractCommitmentDto, SaveContractCommitmentRequest>(group, prefix, "commitments", ContractsModule.Entities.ContractCommitment,
            ContractsModule.Permissions.ContractsRead, ContractsModule.Permissions.CommitmentsWrite);

        var commitments = group.MapGroup("/commitments");

        commitments.MapGet("/occurrences", async (
            Guid? contractId, Guid? partyId, bool? mine, string? status, string? dueFrom, string? dueTo, bool? includeClosed, IContractCommitments service, CancellationToken ct) =>
        {
            var result = await service.OccurrencesAsync(new OccurrenceFilter(contractId, partyId, mine ?? false, status, Date(dueFrom), Date(dueTo), includeClosed ?? false), ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(Map).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        commitments.MapPost("/{id:guid}/occurrences/{occurrenceId:guid}/complete", async (Guid id, Guid occurrenceId, CompleteOccurrenceRequest r, IContractCommitments service, CancellationToken ct) =>
        {
            byte[]? content = null;
            if (!string.IsNullOrEmpty(r.ContentBase64))
            {
                try
                {
                    content = Convert.FromBase64String(r.ContentBase64);
                }
                catch (FormatException)
                {
                    return RecordEndpoints.ToProblem(new RecordError(RecordErrorCode.ValidationFailed, "Dosya içeriği geçersiz.", [new RecordFieldError("file", "Dosya içeriği geçersiz (base64).")]));
                }
            }

            var result = await service.CompleteAsync(id, occurrenceId, new CompleteOccurrenceInput(r.CompletedOn, r.ActualValue, r.Note, r.FileName, content), ct);
            return result.IsSuccess ? Results.Ok(Map(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        commitments.MapPost("/{id:guid}/occurrences/{occurrenceId:guid}/waive", async (Guid id, Guid occurrenceId, SignatureReasonRequest r, IContractCommitments service, CancellationToken ct) =>
        {
            var result = await service.WaiveAsync(id, occurrenceId, r.Reason ?? string.Empty, ct);
            return result.IsSuccess ? Results.Ok(Map(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        commitments.MapPost("/{id:guid}/cancel", async (Guid id, SignatureReasonRequest r, IContractCommitments service, CancellationToken ct) =>
        {
            var result = await service.CancelAsync(id, r.Reason ?? string.Empty, ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<ContractCommitmentDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        commitments.MapPost("/evaluate", async (CommitmentEvaluateRequest r, IContractCommitments service, CancellationToken ct) =>
        {
            var result = await service.EvaluateAsync(r.ContractId, ct);
            return result.IsSuccess
                ? Results.Ok(new CommitmentEvaluationDto(result.Value!.Breached, result.Value.Escalated, result.Value.FollowUpsCreated, result.Value.FollowUpsUnavailable, result.Value.OccurrencesCreated))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        commitments.MapGet("/{id:guid}/occurrences/{occurrenceId:guid}/files/{fileId:guid}", async (Guid id, Guid occurrenceId, Guid fileId, IContractCommitments service, HttpContext http, CancellationToken ct) =>
        {
            var result = await service.GetEvidenceAsync(id, occurrenceId, fileId, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            return Results.File(result.Value!.Content, result.Value.ContentType, result.Value.FileName);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static CommitmentOccurrenceDto Map(OccurrenceView v) => JsonSerializer.Deserialize<CommitmentOccurrenceDto>(JsonSerializer.Serialize(v, Json), Json)!;
}
