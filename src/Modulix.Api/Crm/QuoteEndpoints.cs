using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Settings;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Quotes;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Teklif uç noktaları (/api/crm/quotes, /api/crm/quote-lines; ADR-0019). Başlık ve kalem CRUD'u kayıt boru hattından geçer; durum,
/// onay ve toplam alanları genel uçlardan yazılamaz. Durum geçişleri ayrı eylem uç noktalarıdır ve tek kural kümesini
/// (<see cref="IQuoteWorkflow"/>) kullanır. Eylem izinleri serviste, kayıt bulunduktan SONRA denetlenir: kapsam dışı kayıt 404,
/// izinsiz eylem 403 alır. Eylemler kayıt sürümünü ister (eşzamanlı kabul/iptal/revizyonda yalnızca biri kazanır, diğeri 409 alır).
/// </summary>
internal static class QuoteEndpoints
{
    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<QuoteDto, SaveQuoteRequest>(crm, "quotes", CrmModule.Entities.Quote,
            CrmModule.Permissions.QuotesRead, CrmModule.Permissions.QuotesWrite);
        CrmEndpoints.MapResource<QuoteLineDto, SaveQuoteLineRequest>(crm, "quote-lines", CrmModule.Entities.QuoteLine,
            CrmModule.Permissions.QuotesRead, CrmModule.Permissions.QuotesWrite);

        var quotes = crm.MapGroup("/quotes");
        quotes.MapGet("/{id:guid}/history", HistoryAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/{id:guid}/actions", ActionsAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/settings", SettingsAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        QuoteSharingEndpoints.MapInternal(quotes);

        // Eylem izinleri serviste denetlenir; uç nokta yalnızca okuma iznini ister ki kayıt yoksa 404 dönsün.
        quotes.MapPost("/{id:guid}/submit-for-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.SubmitForApprovalAsync(id, r.Version, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/withdraw-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.WithdrawApprovalAsync(id, r.Version, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/approve", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.ApproveAsync(id, r.Version, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/reject-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RejectApprovalAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/request-changes", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RequestChangesAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/present", async (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, QuoteDocumentService documents, ILogger<QuoteDocumentService> log, CancellationToken ct) =>
        {
            var result = await w.PresentAsync(id, r.Version, ct);
            if (result.IsSuccess)
            {
                // Müşteri belgesi sunum anında sabitlenir (o günkü ünvan/adres/fiyat). Üretilemezse sunum geri alınmaz; belge sonradan
                // POST /documents ile ya da ilk bağlantı oluşturulurken üretilir.
                try
                {
                    var document = await documents.EnsureAsync(id, ct);
                    if (!document.IsSuccess)
                    {
                        log.LogWarning("Teklif {QuoteId} sunuldu ancak müşteri belgesi üretilemedi: {Error}", id, document.Error!.Message);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogError(exception, "Teklif {QuoteId} sunuldu ancak müşteri belgesi üretilirken hata oluştu.", id);
                }
            }

            return Ok(result);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/record-accepted", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RecordAcceptedAsync(id, r.Version, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/record-rejected", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RecordRejectedAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/cancel", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.CancelAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);

        // Revizyon aynı ailede yeni sürüm; çoğaltma yeni aile. İkisi de yeni teklif oluşturma izni (yazma) ister.
        quotes.MapPost("/{id:guid}/revise", async (Guid id, ReviseQuoteRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Created(await w.ReviseAsync(id, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesWrite);
        quotes.MapPost("/{id:guid}/duplicate", async (Guid id, IQuoteWorkflow w, CancellationToken ct) =>
            Created(await w.DuplicateAsync(id, ct))).RequirePermission(CrmModule.Permissions.QuotesWrite);
    }

    private static async Task<IResult> Run(Task<RecordResult<RecordData>> action) => Ok(await action);

    private static IResult Ok(RecordResult<RecordData> result) =>
        result.IsSuccess ? Results.Ok(CrmEndpoints.ToDto<QuoteDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static IResult Created(RecordResult<RecordData> result) =>
        result.IsSuccess
            ? Results.Created($"/api/crm/quotes/{result.Value!.Id}", CrmEndpoints.ToDto<QuoteDto>(result.Value))
            : RecordEndpoints.ToProblem(result.Error!);

    private static async Task<IResult> HistoryAsync(Guid id, IQuoteWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.HistoryAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.Select(h => new QuoteHistoryDto(h.Id, h.Kind, h.At, h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> ActionsAsync(Guid id, IQuoteWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.ActionsAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.Select(a => new QuoteActionStateDto(a.Action, a.Allowed, a.Code, a.Message)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> SettingsAsync(IQuotePolicyProvider provider, CancellationToken ct)
    {
        var s = await provider.GetAsync(ct);
        return Results.Ok(new { s.ApprovalDiscountPercent, s.ApprovalGrossAbove, s.DefaultValidityDays, s.AllowSelfApproval });
    }
}

/// <summary>Teklif politikasını tenant ayarlarından okur (modül ayar altyapısına erişemez).</summary>
internal sealed class QuotePolicyProvider(IServiceProvider services) : IQuotePolicyProvider
{
    public async Task<QuotePolicySettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<ITenantSettings>();
        decimal? Decimal(string? text) =>
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

        var validity = int.TryParse(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteDefaultValidityDays, cancellationToken),
            NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : 30;
        return new QuotePolicySettings(
            Decimal(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteApprovalDiscountPercent, cancellationToken)),
            Decimal(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteApprovalGrossAbove, cancellationToken)),
            validity,
            string.Equals(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteAllowSelfApproval, cancellationToken), "true", StringComparison.OrdinalIgnoreCase));
    }
}
