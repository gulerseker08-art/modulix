using System.Text.Json;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Contracts;

namespace Modulix.Api.Contracts;

/// <summary>
/// Yenileme ve fesih uç noktaları (ADR-0022, dilim 8). İzinler serviste, kayıt bulunduktan sonra denetlenir (kapsam dışı 404, izinsiz 403). Yenileme taslağı, yenilememe kararı ve
/// otomatik uzatma bir dönem için tek karardır (tekrar isteği aynı sonucu döndürür ya da 409 verir); fesih ayrı ve kritik izindir. Zamanlanmış işler arka plan işçisiyle çalışır;
/// <c>POST .../scheduler/run</c> aynı turu çağıranın kimliğiyle elle çalıştırır (yetkisi olanlar için).
/// </summary>
internal static class ContractRenewalEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void MapInternal(RouteGroupBuilder group)
    {
        var contracts = group.MapGroup("/contracts");

        contracts.MapGet("/{id:guid}/renewal", async (Guid id, IContractRenewals service, CancellationToken ct) =>
            Overview(await service.GetAsync(id, ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapPost("/{id:guid}/renewal/draft", async (Guid id, RenewalDraftRequest r, IContractRenewals service, CancellationToken ct) =>
        {
            var result = await service.CreateDraftAsync(id, new RenewalDraftInput(r.StartDate, r.TermMonths), ct);
            return result.IsSuccess
                ? Results.Json(new RenewalDraftResultDto(result.Value!.ContractId, result.Value.Number, result.Value.AlreadyExisted), Json, statusCode: result.Value.AlreadyExisted ? StatusCodes.Status200OK : StatusCodes.Status201Created)
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapPost("/{id:guid}/renewal/decline", async (Guid id, DeclineRenewalRequest r, IContractRenewals service, CancellationToken ct) =>
            Overview(await service.DeclineAsync(id, r.Reason ?? string.Empty, r.Note, ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapPost("/{id:guid}/terminate", async (Guid id, TerminateContractRequest r, IContractRenewals service, CancellationToken ct) =>
            Overview(await service.TerminateAsync(id, new TerminateInput(r.EffectiveDate, r.Reason ?? string.Empty, r.ConfirmShortNotice), ct))).RequirePermission(ContractsModule.Permissions.ContractsRead);

        contracts.MapGet("/{id:guid}/chain", async (Guid id, IContractRenewals service, CancellationToken ct) =>
        {
            var result = await service.ChainAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(i => new ChainItemDto(i.Id, i.Number, i.Title, i.StartDate, i.EndDate, i.EffectiveStatus, i.SignatureStatus, i.Relation)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        group.MapGet("/renewals/calendar", async (int? days, IContractRenewals service, CancellationToken ct) =>
        {
            var result = await service.CalendarAsync(days ?? 120, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(i => new RenewalCalendarItemDto(
                    i.ContractId, i.Number, i.Title, i.PartyId, i.OwnerId, i.EndDate, i.NoticeDeadline, i.DaysToNotice, i.DaysToEnd, i.RenewalMode, i.EffectiveStatus, i.Decision,
                    i.ResultContractId, i.TerminationDate, i.LastReminder)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        group.MapPost("/scheduler/run", async (IContractScheduler scheduler, CancellationToken ct) =>
        {
            var run = await scheduler.RunAsync(ct);
            return Results.Ok(new SchedulerRunDto(
                run.Activated, run.PeriodsExpired, run.Terminated, run.AutoRenewed, run.RemindersCreated, run.RemindersSkipped, run.EscalationsCreated, run.SignaturesExpired,
                run.ApprovalReminders, run.CommitmentsBreached));
        }).RequirePermission(ContractsModule.Permissions.RenewalsManage);
    }

    private static IResult Overview(RecordResult<RenewalOverview> result)
    {
        if (!result.IsSuccess)
        {
            return RecordEndpoints.ToProblem(result.Error!);
        }

        return Results.Ok(JsonSerializer.Deserialize<RenewalOverviewDto>(JsonSerializer.Serialize(result.Value!, Json), Json));
    }
}
