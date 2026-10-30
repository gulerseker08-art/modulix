using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>
/// Teklif–fırsat bağı (ADR-0019, dilim 9). Fırsatın <b>esas teklifi</b> tek bir tekliftir: tutar eşitleme ve raporlama ona bakar. Esas teklif
/// kullanıcı tarafından seçilir ya da fırsatın ilk sunulan teklifi otomatik esas olur (yeni revizyon aynı ailedense esas yeni revizyona geçer).
/// Ayar açıksa (<c>crm.quote.sync-opportunity-amount</c>) açık fırsatın tutarı ve para birimi esas teklifin net toplamıyla eşitlenir; kapalı
/// fırsata dokunulmaz. Fırsat yazımı iş akışı kapısıyla kayıt boru hattından geçer (audit, olay, sürüm denetimi).
/// </summary>
public interface IQuoteOpportunitySync
{
    /// <summary>Esas teklifi seçer (<paramref name="quoteId"/> boşsa kaldırır); teklif aynı fırsata bağlı ve sunulmuş/kabul edilmiş olmalıdır.</summary>
    Task<RecordResult<RecordData>> SetPrimaryAsync(Guid opportunityId, long version, Guid? quoteId, CancellationToken cancellationToken);

    /// <summary>Teklif sunulduğunda çağrılır: esas teklif yoksa bu teklif esas olur; ayar açıksa tutar eşitlenir. Yapılanı özetleyen not döner (yoksa null).</summary>
    Task<string?> OnPresentedAsync(RecordData quote, bool syncAmount, CancellationToken cancellationToken);
}

internal sealed class QuoteOpportunitySync(
    IRecordPipeline pipeline, OpportunityWorkflowGate gate, QuoteReader reader, ICurrentAccess access, IQuotePolicyProvider? policyProvider = null)
    : IQuoteOpportunitySync
{
    private static readonly string[] WritePermissions = [CrmModule.Permissions.OpportunitiesWrite];

    public async Task<RecordResult<RecordData>> SetPrimaryAsync(Guid opportunityId, long version, Guid? quoteId, CancellationToken cancellationToken)
    {
        var opportunity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Opportunity, opportunityId), cancellationToken);
        if (!opportunity.IsSuccess)
        {
            return opportunity;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(CrmModule.Permissions.OpportunitiesWrite))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{CrmModule.Permissions.OpportunitiesWrite}' izni gerekir.");
        }

        Quote? quote = null;
        if (quoteId is { } id)
        {
            // Teklif de çağıranın kapsamında olmalı (boru hattı denetler) ve bu fırsata bağlı, müşteriye sunulmuş ya da kabul edilmiş olmalı.
            if (!(await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, id), cancellationToken)).IsSuccess || await reader.QuoteAsync(id, cancellationToken) is not { } found)
            {
                return Invalid("Teklif bulunamadı.", "quoteId");
            }

            if (found.OpportunityId != opportunityId)
            {
                return Invalid("Teklif bu fırsata bağlı değil.", "quoteId");
            }

            if (found.Status is not (QuoteStatus.Presented or QuoteStatus.Accepted))
            {
                return Invalid("Yalnızca müşteriye sunulmuş ya da kabul edilmiş teklif esas teklif olabilir.", "quoteId");
            }

            quote = found;
        }

        var policy = policyProvider is null ? new QuotePolicySettings() : await policyProvider.GetAsync(cancellationToken);
        var values = new JsonObject { ["primaryQuoteId"] = quoteId?.ToString() };
        if (quote is not null && policy.SyncOpportunityAmount && Text(opportunity.Value!, "status") == OpportunityStatus.Open)
        {
            values["amount"] = quote.NetTotal;
            values["currency"] = quote.Currency;
        }

        using (gate.Open(WritePermissions, quote is null ? "quote.primary-cleared" : "quote.primary-set"))
        {
            return await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.Opportunity, opportunityId, values, version) { Reason = "opportunity.primary-quote" }, cancellationToken);
        }
    }

    public async Task<string?> OnPresentedAsync(RecordData quoteRecord, bool syncAmount, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Text(quoteRecord, "opportunityId"), out var opportunityId) || await reader.QuoteAsync(quoteRecord.Id, cancellationToken) is not { } quote)
        {
            return null;
        }

        var opportunity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Opportunity, opportunityId), cancellationToken);
        if (!opportunity.IsSuccess)
        {
            return "Fırsat bulunamadı ya da erişilemedi; esas teklif ve tutar eşitlenmedi.";
        }

        var currentPrimary = Guid.TryParse(Text(opportunity.Value!, "primaryQuoteId"), out var p) ? p : (Guid?)null;
        var becomesPrimary = currentPrimary is null || currentPrimary == quote.Id || await SameFamilyAsync(currentPrimary.Value, quote, cancellationToken);
        if (!becomesPrimary)
        {
            return "Fırsatın esas teklifi başka bir teklif; bu teklif esas yapılmadı, tutar eşitlenmedi.";
        }

        var open = Text(opportunity.Value!, "status") == OpportunityStatus.Open;
        var values = new JsonObject { ["primaryQuoteId"] = quote.Id.ToString() };
        if (syncAmount && open)
        {
            values["amount"] = quote.NetTotal;
            values["currency"] = quote.Currency;
        }

        RecordResult<RecordData> result;
        using (gate.Open(WritePermissions, "quote.presented"))
        {
            result = await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.Opportunity, opportunityId, values, opportunity.Value!.Version) { Reason = "opportunity.primary-quote" }, cancellationToken);
        }

        if (!result.IsSuccess)
        {
            return $"Fırsat güncellenemedi: {result.Error!.Message}";
        }

        return syncAmount
            ? open
                ? $"Esas teklif olarak bağlandı; fırsat tutarı {quote.NetTotal.ToString("N2", CultureInfo.InvariantCulture)} {quote.Currency} olarak eşitlendi."
                : "Esas teklif olarak bağlandı; fırsat açık olmadığı için tutar eşitlenmedi."
            : "Esas teklif olarak bağlandı (tutar eşitleme kapalı).";
    }

    /// <summary>Yeni revizyon aynı ailedense esas teklif yeni revizyona geçer.</summary>
    private async Task<bool> SameFamilyAsync(Guid currentPrimaryId, Quote quote, CancellationToken cancellationToken) =>
        await reader.QuoteAsync(currentPrimaryId, cancellationToken) is { } current && current.FamilyId == quote.FamilyId;

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static RecordResult<RecordData> Invalid(string message, string field) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
