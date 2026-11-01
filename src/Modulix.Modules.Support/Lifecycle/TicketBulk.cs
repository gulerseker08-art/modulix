using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record BulkTicketResult(Guid Id, string? Number, bool Succeeded, string? Code, string? Message);

public sealed record BulkInput(string? Reason, Guid? OwnerId, Guid? QueueId);

/// <summary>
/// Toplu atama, devir, kapatma ve iptal (ADR-0024 dilim 4). Her talep tek tek ve <b>kendi eyleminin kuralıyla</b> çalışır (izin, veri kapsamı, geçiş tablosu, gerekçe, audit ve tarihçe
/// tek işlemdeki gibi uygulanır); toplu işlem bu kuralları atlamaz. İşlem <b>atomik değildir</b>: her talebin sonucu ayrı döner, başarısız olan diğerlerini geri almaz. En çok
/// <see cref="MaxItems"/> talep, aynı kimlik tekrarlanmaz. Sürüm çakışması denetimi için her talep o anki sürümüyle çalıştırılır (toplu ekran eski sürüm taşımaz).
/// </summary>
public interface ITicketBulk
{
    public const int MaxItems = 50;

    public static readonly IReadOnlyList<string> Actions = [TicketAction.Assign, TicketAction.Transfer, TicketAction.Close, TicketAction.Cancel];

    Task<RecordResult<IReadOnlyList<BulkTicketResult>>> RunAsync(string action, IReadOnlyList<Guid> ids, BulkInput input, CancellationToken cancellationToken);
}

internal sealed class TicketBulk(IRecordPipeline pipeline, ITicketWorkflow workflow) : ITicketBulk
{
    public async Task<RecordResult<IReadOnlyList<BulkTicketResult>>> RunAsync(string action, IReadOnlyList<Guid> ids, BulkInput input, CancellationToken cancellationToken)
    {
        if (!ITicketBulk.Actions.Contains(action))
        {
            return Invalid("action", $"Toplu işlem şunlardan biri olmalıdır: {string.Join(", ", ITicketBulk.Actions)}.");
        }

        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return Invalid("ids", "En az bir talep seçin.");
        }

        if (distinct.Count > ITicketBulk.MaxItems)
        {
            return Invalid("ids", $"Bir seferde en fazla {ITicketBulk.MaxItems} talep işlenebilir.");
        }

        var results = new List<BulkTicketResult>(distinct.Count);
        foreach (var id in distinct)
        {
            var loaded = await pipeline.GetAsync(new RecordRef(SupportModule.Entities.Ticket, id), cancellationToken);
            if (!loaded.IsSuccess)
            {
                // Kapsam dışı/başka tenant talebi ile var olmayan talep aynı yanıtı verir.
                results.Add(new BulkTicketResult(id, null, false, loaded.Error!.Code.ToString(), loaded.Error.Message));
                continue;
            }

            var number = TicketWorkflow.Text(loaded.Value!, "number");
            var result = await workflow.RunAsync(id, action, new TicketActionInput(loaded.Value!.Version, input.Reason, input.OwnerId, input.QueueId, null, null, null, null, null), cancellationToken);
            results.Add(result.IsSuccess
                ? new BulkTicketResult(id, number, true, null, null)
                : new BulkTicketResult(id, number, false, result.Error!.Code.ToString(), result.Error.Message));
        }

        return RecordResult<IReadOnlyList<BulkTicketResult>>.Ok(results);
    }

    private static RecordResult<IReadOnlyList<BulkTicketResult>> Invalid(string field, string message) =>
        RecordResult<IReadOnlyList<BulkTicketResult>>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
