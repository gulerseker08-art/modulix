using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Lifecycle;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;

namespace Modulix.Api.Logistics;

/// <summary>
/// Lojistik iş akışı uçları (ADR-0038 K7, dilim 4–5): taşıma talebi, sevkiyat ve seferin durum eylemleri; seferin uygunluk denetimi ve
/// sevkiyat ekleme/çıkarma. Kayıtlar genel kayıt uçlarındadır
/// (<c>/api/records/logistics.transport-request</c>, <c>/api/records/logistics.shipment</c>); durum yalnızca buradaki komutlarla değişir.
/// Eylemin izni serviste denetlenir (talep onayı ayrı izindir); tenant oturumdan gelir. Salt okunur lisans evresinde POST 403'tür.
/// </summary>
public static class LogisticsEndpoints
{
    private static IResult ToResponse(Modulix.Core.Contracts.Records.RecordResult<Modulix.Core.Contracts.Records.RecordData> result) =>
        result.IsSuccess
            ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
            : RecordEndpoints.ToProblem(result.Error!);

    private static readonly Dictionary<string, string> EntityOf = new(StringComparer.Ordinal)
    {
        ["requests"] = LogisticsModule.Entities.TransportRequest,
        ["shipments"] = LogisticsModule.Entities.Shipment,
        ["trips"] = LogisticsModule.Entities.Trip,
        ["expenses"] = LogisticsModule.Entities.TripExpense,
    };

    public static IEndpointRouteBuilder MapLogisticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/logistics")
            .RequireAuthorization()
            .RequireModule(LogisticsModule.ModuleKey)
            .WithTags("Lojistik");

        group.MapGet("/{kind}/{id:guid}/actions", async (string kind, Guid id, ILogisticsWorkflow workflow, CancellationToken ct) =>
        {
            if (!EntityOf.TryGetValue(kind, out var entityKey))
            {
                return Results.NotFound();
            }

            var result = await workflow.ActionsAsync(entityKey, id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new LogisticsActionDto(a.Action, a.Label, a.Allowed, a.Message, a.RequiresReason, a.Destructive)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPost("/{kind}/{id:guid}/actions", async (string kind, Guid id, RunLogisticsActionRequest request, ILogisticsWorkflow workflow, CancellationToken ct) =>
        {
            if (!EntityOf.TryGetValue(kind, out var entityKey))
            {
                return Results.NotFound();
            }

            var result = await workflow.RunAsync(entityKey, id, request.Action ?? string.Empty, request.Version, request.Reason, ct);
            return result.IsSuccess
                ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        // Teslim (dilim 6): yoldaki sevkiyat teslim edilir, kısmen teslim edilir ya da teslim edilemez. Değişmez teslim kanıtı, durum ve iş olayı tek işlemde
        // yazılır. Yazan POST salt okunur lisans evresinde 403'tür; sunucu zamanı verir, istemci zamanı yok sayılır.
        group.MapPost("/shipments/{id:guid}/delivery", async (Guid id, RecordDeliveryRequest request, IShipmentDelivery delivery, CancellationToken ct) =>
        {
            var result = await delivery.RecordAsync(
                id, request.Version, new DeliveryCommand(request.Outcome ?? string.Empty, request.ReceivedBy, request.Damaged, request.Shortage, request.FailureReason, request.Notes), ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var (shipment, proof) = (result.Value!.Shipment, result.Value.Proof);
            return Results.Ok(new DeliveryResponse(
                new RecordResponse(shipment.EntityKey, shipment.Id, shipment.Version, shipment.Values),
                new RecordResponse(proof.EntityKey, proof.Id, proof.Version, proof.Values)));
        });

        // Sefer planlama (dilim 5): uygunluk denetimi, sevkiyat ekleme ve çıkarma. Yazma izni ve sefer durumu serviste denetlenir.
        // Operasyon panosu (dilim 9a): bölümler okuma iznine göre gelir; hiçbir bölümü okuyamayan için 403.
        group.MapGet("/dashboard", async (Modulix.Modules.Logistics.Dashboard.ILogisticsDashboard dashboard, CancellationToken ct) =>
        {
            var result = await dashboard.GetAsync(ct);
            return result.IsEmpty
                ? Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Yetkiniz yok.", detail: "Panoyu görmek için talep, sevkiyat, sefer ya da sürücü okuma izni gerekir.")
                : Results.Ok(result);
        });

        group.MapGet("/trips/{id:guid}/check", async (Guid id, ITripPlanner planner, CancellationToken ct) =>
        {
            var result = await planner.CheckAsync(id, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var (issues, load) = (result.Value!.Issues, result.Value.Load);
            return Results.Ok(new TripCheckDto(
                result.Value.CanConfirm,
                [.. issues.Select(i => new TripCheckIssueDto(i.Code, i.Message, i.Blocking))],
                new TripLoadDto(load.ShipmentCount, load.StopCount, load.WeightKg, load.PayloadKg, load.Pallets, load.PalletCapacity, load.LoadingMeters,
                    load.LoadingMetersCapacity, load.VolumeM3, load.VolumeCapacity)));
        });

        group.MapPost("/trips/{id:guid}/shipments", async (Guid id, AssignShipmentRequest request, ITripPlanner planner, CancellationToken ct) =>
            ToResponse(await planner.AssignAsync(id, request.ShipmentId, ct)));

        group.MapDelete("/trips/{id:guid}/shipments/{shipmentId:guid}", async (Guid id, Guid shipmentId, ITripPlanner planner, CancellationToken ct) =>
            ToResponse(await planner.UnassignAsync(id, shipmentId, ct)));

        return app;
    }
}

/// <summary>Lojistik politikası: tenant ayarlarından okunur (modül Core'un ayar servisini görmez; host bu köprüyü sağlar).</summary>
internal sealed class LogisticsPolicyProvider(IServiceProvider services) : ILogisticsPolicyProvider
{
    public async Task<string> CredentialCheckAsync(CancellationToken cancellationToken)
    {
        var value = await services.GetRequiredService<Modulix.Core.Contracts.Settings.ITenantSettingReader>()
            .GetValueAsync(LogisticsModule.SettingKeys.CredentialCheck, cancellationToken);
        return value == LogisticsModule.CredentialCheckModes.Block ? LogisticsModule.CredentialCheckModes.Block : LogisticsModule.CredentialCheckModes.Warn;
    }

    public async Task<string?> DefaultCountryAsync(CancellationToken cancellationToken)
    {
        var value = await services.GetRequiredService<Modulix.Core.Contracts.Settings.ITenantSettingReader>()
            .GetValueAsync(LogisticsModule.SettingKeys.DefaultCountry, cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
