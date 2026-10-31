using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Tarihçe satırı (API görünümü).</summary>
public sealed record ContractHistoryItem(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>
/// Sözleşme iş akışı: durum geçişleri ve tarihçe. İlk dilimde yalnızca tarihçe okunur; geçiş eylemleri ikinci dilimde eklenir.
/// Her çağrı önce sözleşmeyi kayıt boru hattından okur: kapsam dışı ya da başka tenant'ın sözleşmesi "bulunamadı" alır.
/// </summary>
public interface IContractWorkflow
{
    Task<RecordResult<IReadOnlyList<ContractHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class ContractWorkflow(IRecordPipeline pipeline, ContractReader reader) : IContractWorkflow
{
    public async Task<RecordResult<IReadOnlyList<ContractHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, id), cancellationToken);
        if (!contract.IsSuccess)
        {
            return RecordResult<IReadOnlyList<ContractHistoryItem>>.Fail(contract.Error!.Code, contract.Error.Message, contract.Error.FieldErrors);
        }

        var events = await reader.HistoryAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<ContractHistoryItem>>.Ok(
            [.. events.Select(e => new ContractHistoryItem(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.Reason))]);
    }
}
