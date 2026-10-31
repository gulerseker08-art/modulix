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
        return new ContractPolicySettings(
            string.Equals(await settings.GetValueAsync(ContractsModule.SettingKeys.AllowSelfApproval, cancellationToken), "true", StringComparison.OrdinalIgnoreCase));
    }
}
