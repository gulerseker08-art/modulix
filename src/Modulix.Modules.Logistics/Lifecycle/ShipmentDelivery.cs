using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics.Lifecycle;

/// <summary>Teslim komutunun girdisi (ADR-0038 K8). Zaman sunucuda verilir; fotoğraf ve imza dosyaları kanıt kaydına ayrıca eklenir.</summary>
public sealed record DeliveryCommand(string Outcome, string? ReceivedBy, bool Damaged, bool Shortage, string? FailureReason, string? Notes);

/// <summary>Teslimin sonucu: güncel sevkiyat ve yazılan kanıt.</summary>
public sealed record DeliveryResult(RecordData Shipment, RecordData Proof);

/// <summary>
/// Teslim komutu (ADR-0038 K7, K8, dilim 6). Yoldaki sevkiyat teslim edilir, kısmen teslim edilir ya da teslim edilemez; üç sonuç yalnızca bu komutla
/// verilir (genel eylem ucu teslim durumlarına geçmez). Komut tek işlemde (<see cref="IRecordPipeline.RunAtomicAsync{T}"/>) değişmez teslim kanıtını
/// yazar, sevkiyatın durumunu değiştirir ve iş olayını yayınlar: birlikte yazılır ya da hiçbiri. Teslim edildi ve kısmi teslim
/// <c>logistics.shipment.delivered</c> (ADR-0037 teslim → faturalanabilir kalem ve kural tetikleyicisinin dayanağı), başarısız teslim
/// <c>logistics.shipment.delivery-failed</c> olayını yayınlar. Kayıt boru hattının izin, sürüm denetimi (409), denetim kaydı ve outbox kuralları geçerlidir.
/// </summary>
public interface IShipmentDelivery
{
    /// <summary>Teslimi kaydeder. Bilinmeyen sonuç ve eksik zorunlu alan 422; sevkiyat yolda değilse ya da sürüm eskiyse 409; izin yoksa 403.</summary>
    Task<RecordResult<DeliveryResult>> RecordAsync(Guid shipmentId, long version, DeliveryCommand command, CancellationToken cancellationToken);
}

internal sealed class ShipmentDelivery(
    IRecordPipeline pipeline, LogisticsWorkflowGate gate, ICurrentAccess access, IBusinessEventPublisher events) : IShipmentDelivery
{
    public async Task<RecordResult<DeliveryResult>> RecordAsync(Guid shipmentId, long version, DeliveryCommand command, CancellationToken cancellationToken)
    {
        // Önce kayıt okunur: göremeyen için sevkiyat yoktur (404), izin denetimi varlığı sızdırmaz.
        var shipment = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Shipment, shipmentId), cancellationToken);
        if (!shipment.IsSuccess)
        {
            return RecordResult<DeliveryResult>.Fail(shipment.Error!.Code, shipment.Error.Message, shipment.Error.FieldErrors);
        }

        if (!await access.HasPermissionAsync(LogisticsModule.Permissions.ShipmentsWrite, cancellationToken))
        {
            return RecordResult<DeliveryResult>.Fail(RecordErrorCode.PermissionDenied, "Teslim kaydetmek için yetkiniz yok.");
        }

        if (!DeliveryProof.Outcomes.All.Contains(command.Outcome))
        {
            return RecordResult<DeliveryResult>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen teslim sonucu.", [new RecordFieldError("outcome", "Bilinmeyen teslim sonucu.")]);
        }

        if (shipment.Value!.Values["status"]?.GetValue<string>() != Shipment.Statuses.InTransit)
        {
            return RecordResult<DeliveryResult>.Fail(RecordErrorCode.Conflict, "Yalnızca yoldaki sevkiyat için teslim kaydedilir.");
        }

        return await pipeline.RunAtomicAsync(ct => RecordCoreAsync(shipment.Value!, version, command, ct), cancellationToken);
    }

    private async Task<RecordResult<DeliveryResult>> RecordCoreAsync(RecordData shipment, long version, DeliveryCommand command, CancellationToken cancellationToken)
    {
        var proofValues = new JsonObject
        {
            ["shipmentId"] = shipment.Id.ToString(),
            ["outcome"] = command.Outcome,
            ["receivedBy"] = command.ReceivedBy,
            ["damaged"] = command.Damaged,
            ["shortage"] = command.Shortage,
            ["failureReason"] = command.FailureReason,
            ["notes"] = command.Notes,
        };

        RecordResult<RecordData> proof;
        using (gate.Open(LogisticsModule.Permissions.ShipmentsWrite, status: null, reason: null))
        {
            proof = await pipeline.CreateAsync(new RecordCommand(LogisticsModule.Entities.DeliveryProof, null, proofValues), cancellationToken);
        }

        if (!proof.IsSuccess)
        {
            return RecordResult<DeliveryResult>.Fail(proof.Error!.Code, proof.Error.Message, proof.Error.FieldErrors);
        }

        var status = StatusOf(command.Outcome);
        RecordResult<RecordData> updated;
        using (gate.Open(LogisticsModule.Permissions.ShipmentsWrite, status, StatusReason(command)))
        {
            updated = await pipeline.UpdateAsync(
                new RecordCommand(LogisticsModule.Entities.Shipment, shipment.Id, new JsonObject { ["status"] = status }, version), cancellationToken);
        }

        if (!updated.IsSuccess)
        {
            return RecordResult<DeliveryResult>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
        }

        var failed = command.Outcome == DeliveryProof.Outcomes.Failed;
        await events.PublishAsync(
            new BusinessEvent(failed ? LogisticsModule.Events.ShipmentDeliveryFailed : LogisticsModule.Events.ShipmentDelivered, shipment.Id, Payload(updated.Value!, proof.Value!, failed))
            {
                RecordVersion = updated.Value!.Version,
            }, cancellationToken);

        return RecordResult<DeliveryResult>.Ok(new DeliveryResult(updated.Value!, proof.Value!));
    }

    private static string StatusOf(string outcome) => outcome switch
    {
        DeliveryProof.Outcomes.Delivered => Shipment.Statuses.Delivered,
        DeliveryProof.Outcomes.Partial => Shipment.Statuses.Partial,
        _ => Shipment.Statuses.Failed,
    };

    /// <summary>Sevkiyatın son geçiş notu: kısmi teslimde açıklama, başarısız teslimde neden (uzunsa kısaltılır).</summary>
    private static string? StatusReason(DeliveryCommand command)
    {
        var text = command.Outcome == DeliveryProof.Outcomes.Failed ? command.FailureReason : command.Outcome == DeliveryProof.Outcomes.Partial ? command.Notes : null;
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return text is { Length: > CargoEntity.ReasonMaxLength } ? text[..(CargoEntity.ReasonMaxLength - 1)] + "…" : text;
    }

    /// <summary>
    /// İş olayının yükü: beyan edilmiş alanlar. Sevkiyat alanları komutun döndürdüğü kayıttan, teslim alanları kanıttan gelir (kayıtta
    /// karşılığı olmayan türetilmiş alanlardır, hassas sayılmaz).
    /// </summary>
    private static JsonObject Payload(RecordData shipment, RecordData proof, bool failed)
    {
        var payload = new JsonObject();
        foreach (var field in LogisticsModule.Events.ShipmentFields)
        {
            if (shipment.Values.TryGetPropertyValue(field, out var value) && value is not null)
            {
                payload[field] = value.DeepClone();
            }
        }

        payload["proofId"] = proof.Id.ToString();
        payload["outcome"] = proof.Values["outcome"]?.DeepClone();
        payload["receivedAt"] = proof.Values["receivedAt"]?.DeepClone();
        if (failed)
        {
            payload["failureReason"] = proof.Values["failureReason"]?.DeepClone();
        }
        else
        {
            payload["receivedBy"] = proof.Values["receivedBy"]?.DeepClone();
            payload["damaged"] = proof.Values["damaged"]?.DeepClone();
            payload["shortage"] = proof.Values["shortage"]?.DeepClone();
        }

        return payload;
    }
}
