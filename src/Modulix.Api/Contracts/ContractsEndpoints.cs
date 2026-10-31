using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Contracts;

namespace Modulix.Api.Contracts;

/// <summary>
/// Sözleşme uç noktaları (/api/contracts/...; ADR-0021). Başlık, taraf ve kalem CRUD'u kayıt boru hattından geçer; durum, sürüm ve
/// toplam alanları genel uçlardan yazılamaz. Durum geçişleri ikinci dilimde ayrı eylem uç noktalarıdır.
/// </summary>
public static class ContractsEndpoints
{
    private const string Prefix = "/api/contracts";

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

        group.MapGet("/contracts/{id:guid}/history", async (Guid id, IContractWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.HistoryAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(h => new ContractHistoryDto(h.Id, h.Kind, h.At, h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        return app;
    }
}
