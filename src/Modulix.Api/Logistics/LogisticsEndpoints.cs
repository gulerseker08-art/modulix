using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Lifecycle;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;

namespace Modulix.Api.Logistics;

/// <summary>
/// Lojistik iş akışı uçları (ADR-0038 K7, dilim 4): taşıma talebi ve sevkiyatın durum eylemleri. Kayıtlar genel kayıt uçlarındadır
/// (<c>/api/records/logistics.transport-request</c>, <c>/api/records/logistics.shipment</c>); durum yalnızca buradaki komutlarla değişir.
/// Eylemin izni serviste denetlenir (talep onayı ayrı izindir); tenant oturumdan gelir. Salt okunur lisans evresinde POST 403'tür.
/// </summary>
public static class LogisticsEndpoints
{
    private static readonly Dictionary<string, string> EntityOf = new(StringComparer.Ordinal)
    {
        ["requests"] = LogisticsModule.Entities.TransportRequest,
        ["shipments"] = LogisticsModule.Entities.Shipment,
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

        return app;
    }
}
