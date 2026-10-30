using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>Tenant ayarlarından gelen teklif politikası (API sağlar; modül ayar altyapısına erişemez).</summary>
/// <param name="ApprovalDiscountPercent">Toplam indirim bu yüzdeyi aşarsa iç onay zorunlu; boşsa indirimden onay gerekmez.</param>
/// <param name="ApprovalGrossAbove">Genel toplam bu tutarı aşarsa iç onay zorunlu (teklif para biriminde); boşsa sınır yok.</param>
/// <param name="AllowSelfApproval">Teklifi onaya gönderen kişi kendi teklifini onaylayabilir mi? Varsayılan hayır.</param>
/// <param name="OnAcceptWinOpportunity">Teklif kabul edilince bağlı açık fırsat otomatik "kazanıldı" yapılsın mı? Varsayılan hayır (davranış değişmez).</param>
/// <param name="FollowUpDays">Teklif sunulunca bu kadar gün sonrasına sorumlu için takip aktivitesi açılır; 0 = kapalı.</param>
/// <param name="ExpiryReminderDays">Geçerliliği bitmeden bu kadar gün önce sorumlu için hatırlatma aktivitesi açılır; 0 = kapalı.</param>
/// <param name="SyncOpportunityAmount">Esas teklif sunulunca açık fırsatın tutarı teklifin net toplamıyla eşitlensin mi? Varsayılan hayır.</param>
public sealed record QuotePolicySettings(
    decimal? ApprovalDiscountPercent = null, decimal? ApprovalGrossAbove = null, int DefaultValidityDays = 30, bool AllowSelfApproval = false,
    bool OnAcceptWinOpportunity = false, int FollowUpDays = 0, int ExpiryReminderDays = 0, bool SyncOpportunityAmount = false);

public interface IQuotePolicyProvider
{
    Task<QuotePolicySettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Teklif tarihçesi satırı.</summary>
public sealed record QuoteHistoryItem(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. Yetkisi olmayan eylem listede hiç yer almaz.</summary>
public sealed record QuoteActionState(string Action, bool Allowed, string? Code, string? Message);

/// <summary>
/// Teklif yaşam döngüsü eylemleri (ADR-0019). Tek yazma yolu: iş akışı kapısı eylemin izniyle açılır ve kayıt boru hattı üzerinden
/// güncelleme yapılır; kapsam, hassas alan, audit, olay ve sürüm denetimi (409) depoda uygulanır. Her eylem kaydın sürümünü ister:
/// eşzamanlı kabul/iptal/revizyon yarışında yalnızca biri kazanır. Kurallar <see cref="QuoteRules"/> ile arayüzle ortaktır.
/// </summary>
public interface IQuoteWorkflow
{
    Task<RecordResult<RecordData>> SubmitForApprovalAsync(Guid id, long version, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> WithdrawApprovalAsync(Guid id, long version, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> ApproveAsync(Guid id, long version, string? note, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RejectApprovalAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RequestChangesAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> PresentAsync(Guid id, long version, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RecordAcceptedAsync(Guid id, long version, string? note, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RecordRejectedAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CancelAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    /// <summary>Aynı ailede yeni taslak revizyon; kaynağın kalemleri aynı kararlı anahtarlarla kopyalanır.</summary>
    Task<RecordResult<RecordData>> ReviseAsync(Guid id, string reason, CancellationToken cancellationToken);

    /// <summary>Yeni teklif ailesi olarak çoğaltma; revizyon değildir.</summary>
    Task<RecordResult<RecordData>> DuplicateAsync(Guid id, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<QuoteHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Kullanıcının bu teklif için görebileceği eylemler ve yapılamayanların açıklaması.</summary>
    Task<RecordResult<IReadOnlyList<QuoteActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Teklifin iç onay gereksinimi: eşleşen kurallar, eşik gerekçesi, onaylayan kısıtı ve geçerli kullanıcının karar yetkisi.</summary>
    Task<RecordResult<QuoteApprovalRequirement>> ApprovalRequirementsAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class QuoteWorkflow(
    IRecordPipeline pipeline,
    QuoteWorkflowGate gate,
    ICurrentAccess access,
    QuoteReader reader,
    TimeProvider time,
    ITenantContextAccessor tenant,
    IQuotePolicyProvider? policyProvider = null) : IQuoteWorkflow
{
    private const string Key = CrmModule.Entities.Quote;
    private const string RequiredApproval = "Required";

    public async Task<RecordResult<RecordData>> SubmitForApprovalAsync(Guid id, long version, CancellationToken cancellationToken)
    {
        // Onaya gönderimde eşleşen kurallar ve kural sürümleri tarihçeye yazılır: onay hangi kurala göre istendi, sonradan izlenir.
        string? note = null;
        if ((await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken)) is { IsSuccess: true } loaded)
        {
            var requirement = await RequirementAsync(loaded.Value!, cancellationToken);
            note = Truncate(QuoteApprovalEvaluator.HistoryNote(requirement.Matches, requirement.ThresholdReason), Quote.ReasonMaxLength);
        }

        return await Transition(id, version, QuoteAction.SubmitForApproval, CrmModule.Permissions.QuotesWrite, note, (q, _) => new JsonObject
        {
            ["approvalStatus"] = QuoteApprovalStatus.Pending,
        }, "SubmittedForApproval", cancellationToken);
    }

    public Task<RecordResult<RecordData>> WithdrawApprovalAsync(Guid id, long version, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.WithdrawApproval, CrmModule.Permissions.QuotesWrite, null, (q, _) => new JsonObject
        {
            ["approvalStatus"] = QuoteApprovalStatus.NotRequired,
        }, "ApprovalWithdrawn", cancellationToken);

    public Task<RecordResult<RecordData>> ApproveAsync(Guid id, long version, string? note, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.Approve, CrmModule.Permissions.QuotesApprove, note, (q, _) => new JsonObject
        {
            ["approvalStatus"] = QuoteApprovalStatus.Approved,
            ["statusReason"] = Trim(note),
        }, "Approved", cancellationToken, checkSelfApproval: true, checkApprover: true);

    public Task<RecordResult<RecordData>> RejectApprovalAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.RejectApproval, CrmModule.Permissions.QuotesApprove, reason, (q, _) => new JsonObject
        {
            ["approvalStatus"] = QuoteApprovalStatus.Rejected,
            ["statusReason"] = Trim(reason),
        }, "ApprovalRejected", cancellationToken, reasonRequired: true, checkSelfApproval: true, checkApprover: true);

    public Task<RecordResult<RecordData>> RequestChangesAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.RequestChanges, CrmModule.Permissions.QuotesApprove, reason, (q, _) => new JsonObject
        {
            ["approvalStatus"] = QuoteApprovalStatus.ChangesRequested,
            ["statusReason"] = Trim(reason),
        }, "ChangesRequested", cancellationToken, reasonRequired: true, checkSelfApproval: true, checkApprover: true);

    public Task<RecordResult<RecordData>> PresentAsync(Guid id, long version, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.Present, CrmModule.Permissions.QuotesPresent, null, (q, now) => new JsonObject
        {
            ["status"] = QuoteStatus.Presented,
            ["presentedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            // Sunulan belgenin müşteri ünvanı o anda sabitlenir; firma kartı sonradan değişse de belge aynı kalır.
            ["customerNameSnapshot"] = Text(q, "customerName"),
        }, "Presented", cancellationToken, enrichWithCompanyName: true);

    public Task<RecordResult<RecordData>> RecordAcceptedAsync(Guid id, long version, string? note, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.Accept, CrmModule.Permissions.QuotesRecordDecision, note, (q, now) => new JsonObject
        {
            ["status"] = QuoteStatus.Accepted,
            ["decidedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["statusReason"] = Trim(note),
        }, "Accepted", cancellationToken);

    public Task<RecordResult<RecordData>> RecordRejectedAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.Reject, CrmModule.Permissions.QuotesRecordDecision, reason, (q, now) => new JsonObject
        {
            ["status"] = QuoteStatus.Rejected,
            ["decidedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["statusReason"] = Trim(reason),
        }, "Rejected", cancellationToken, reasonRequired: true);

    public Task<RecordResult<RecordData>> CancelAsync(Guid id, long version, string reason, CancellationToken cancellationToken) =>
        Transition(id, version, QuoteAction.Cancel, CrmModule.Permissions.QuotesCancel, reason, (q, now) => new JsonObject
        {
            ["status"] = QuoteStatus.Cancelled,
            ["decidedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["statusReason"] = Trim(reason),
        }, "Cancelled", cancellationToken, reasonRequired: true);

    public async Task<RecordResult<RecordData>> ReviseAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Revizyon gerekçesi zorunludur.", "reason");
        }

        var source = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!source.IsSuccess)
        {
            return source;
        }

        var values = CopyValues(source.Value!);
        values["revisionOfId"] = id.ToString();
        values["validUntil"] = null;
        return await pipeline.CreateAsync(new RecordCommand(Key, null, values) { Reason = "quote.revised" }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> DuplicateAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!source.IsSuccess)
        {
            return source;
        }

        var values = CopyValues(source.Value!);
        values["duplicateOfId"] = id.ToString();
        values["validUntil"] = null;
        values["opportunityId"] = source.Value!.Values["opportunityId"]?.DeepClone();
        return await pipeline.CreateAsync(new RecordCommand(Key, null, values) { Reason = "quote.duplicated" }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<QuoteHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        // Okuma izni ve veri kapsamı boru hattında uygulanır: teklifi göremeyen tarihçesini de göremez.
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<QuoteHistoryItem>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var rows = await reader.HistoryAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<QuoteHistoryItem>>.Ok([.. rows.Select(e => new QuoteHistoryItem(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.Reason))]);
    }

    public async Task<RecordResult<IReadOnlyList<QuoteActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<QuoteActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        var quote = record.Value!;
        var hasLines = await HasLinesAsync(id, cancellationToken);
        var approval = await EffectiveApprovalAsync(quote, cancellationToken);
        var now = time.GetUtcNow();
        var actions = new (string Action, string Permission)[]
        {
            (QuoteAction.SubmitForApproval, CrmModule.Permissions.QuotesWrite),
            (QuoteAction.WithdrawApproval, CrmModule.Permissions.QuotesWrite),
            (QuoteAction.Approve, CrmModule.Permissions.QuotesApprove),
            (QuoteAction.RejectApproval, CrmModule.Permissions.QuotesApprove),
            (QuoteAction.RequestChanges, CrmModule.Permissions.QuotesApprove),
            (QuoteAction.Present, CrmModule.Permissions.QuotesPresent),
            (QuoteAction.Accept, CrmModule.Permissions.QuotesRecordDecision),
            (QuoteAction.Reject, CrmModule.Permissions.QuotesRecordDecision),
            (QuoteAction.Cancel, CrmModule.Permissions.QuotesCancel),
        };

        // Yetkisi olmayan eylem hiç gösterilmez; yetkisi olup duruma uymayan eylem nedeniyle birlikte döner.
        var list = new List<QuoteActionState>();
        foreach (var (action, permission) in actions.Where(a => granted.Contains(a.Permission)))
        {
            var rule = QuoteRules.Can(action, Status(quote), approval, ValidUntil(quote), now, hasLines);
            if (rule.Allowed && action is QuoteAction.Approve or QuoteAction.RejectApproval or QuoteAction.RequestChanges
                && await ApproverRestrictionAsync(quote, cancellationToken) is { } restriction)
            {
                rule = QuoteRuleResult.Deny("approver-restricted", restriction);
            }

            list.Add(new QuoteActionState(action, rule.Allowed, rule.Code, rule.Message));
        }

        return RecordResult<IReadOnlyList<QuoteActionState>>.Ok(list);
    }

    // ---- yardımcılar

    private async Task<RecordResult<RecordData>> Transition(
        Guid id, long version, string action, string permission, string? reason, Func<RecordData, DateTimeOffset, JsonObject> build, string eventKind,
        CancellationToken cancellationToken, bool reasonRequired = false, bool checkSelfApproval = false, bool enrichWithCompanyName = false, bool checkApprover = false)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var quote = loaded.Value!;
        if (reasonRequired && string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Gerekçe zorunludur.", "reason");
        }

        var now = time.GetUtcNow();
        var rule = QuoteRules.Can(action, Status(quote), await EffectiveApprovalAsync(quote, cancellationToken), ValidUntil(quote), now, await HasLinesAsync(id, cancellationToken));
        if (!rule.Allowed)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.",
                [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        if (checkSelfApproval && !(await PolicyAsync(cancellationToken)).AllowSelfApproval && await SubmittedByCurrentUserAsync(id, cancellationToken))
        {
            return Invalid("Kendi gönderdiğiniz teklifi onaylayamazsınız.", "approvalStatus");
        }

        if (checkApprover && await ApproverRestrictionAsync(quote, cancellationToken) is { } restriction)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, restriction);
        }

        if (enrichWithCompanyName && Guid.TryParse(Text(quote, "companyId"), out var companyId))
        {
            var company = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Company, companyId), cancellationToken);
            quote.Values["customerName"] = company.IsSuccess ? company.Value!.Values["name"]?.DeepClone() : null;
        }

        var values = build(quote, now);
        RecordResult<RecordData> result;
        using (gate.Open([permission], eventKind, Trim(reason)))
        {
            result = await pipeline.UpdateAsync(new RecordCommand(Key, id, values, version) { Reason = $"quote.{eventKind}" }, cancellationToken);
        }

        return result;
    }

    private async Task<bool> SubmittedByCurrentUserAsync(Guid id, CancellationToken cancellationToken) =>
        await reader.LastSubmitterAsync(id, cancellationToken) is { } submitter && submitter == tenant.Current?.UserId;

    private async Task<string> EffectiveApprovalAsync(RecordData quote, CancellationToken cancellationToken)
    {
        var approval = Text(quote, "approvalStatus") ?? QuoteApprovalStatus.NotRequired;
        if (approval != QuoteApprovalStatus.NotRequired)
        {
            return approval;
        }

        // Genel eşik ayarı ya da onay kuralları onay gerektiriyorsa ve onay alınmadıysa sunum engellenir.
        return (await RequirementAsync(quote, cancellationToken)).Required ? RequiredApproval : approval;
    }

    public async Task<RecordResult<QuoteApprovalRequirement>> ApprovalRequirementsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        return record.IsSuccess
            ? RecordResult<QuoteApprovalRequirement>.Ok(await RequirementAsync(record.Value!, cancellationToken))
            : RecordResult<QuoteApprovalRequirement>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
    }

    /// <summary>Genel eşik ayarı (varsa) ve eşleşen kurallar birlikte: biri yeterse onay gerekir.</summary>
    private async Task<QuoteApprovalRequirement> RequirementAsync(RecordData quote, CancellationToken cancellationToken)
    {
        var policy = await PolicyAsync(cancellationToken);
        var list = Number(quote, "listTotal");
        var discount = Number(quote, "discountTotal");
        var percent = QuoteApprovalEvaluator.DiscountPercent(list, discount);
        var gross = Number(quote, "grossTotal");
        var currency = Text(quote, "currency") ?? "TRY";

        string? threshold = null;
        if (policy.ApprovalDiscountPercent is { } limit && percent > limit)
        {
            threshold = $"Genel ayar: indirim %{percent:0.##} (eşik %{limit:0.##})";
        }
        else if (policy.ApprovalGrossAbove is { } above && gross > above)
        {
            threshold = $"Genel ayar: genel toplam {gross:N2} {currency} (eşik {above:N2})";
        }

        var matches = QuoteApprovalEvaluator.Match(await reader.ActiveRulesAsync(cancellationToken), currency, list, discount, gross);
        var restricted = QuoteApprovalEvaluator.RestrictedApprovers(matches);
        var user = tenant.Current?.UserId;
        var canDecide = restricted.Count == 0 || (user is { } id && restricted.Contains(id));
        var note = restricted.Count > 0 && !canDecide ? "Bu teklifin onayı kural gereği belirli onaylayanlara atanmıştır." : null;
        return new QuoteApprovalRequirement(threshold is not null || matches.Count > 0, matches, threshold, restricted.Count > 0, canDecide, note);
    }

    /// <summary>Onay belirli onaylayanlara atanmışsa ve geçerli kullanıcı onlardan değilse engel mesajı döner.</summary>
    private async Task<string?> ApproverRestrictionAsync(RecordData quote, CancellationToken cancellationToken)
    {
        var requirement = await RequirementAsync(quote, cancellationToken);
        return requirement.Restricted && !requirement.CanDecide ? requirement.Note : null;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    private async Task<QuotePolicySettings> PolicyAsync(CancellationToken cancellationToken) =>
        policyProvider is null ? new QuotePolicySettings() : await policyProvider.GetAsync(cancellationToken);

    private Task<bool> HasLinesAsync(Guid id, CancellationToken cancellationToken) => reader.HasIncludedLinesAsync(id, cancellationToken);

    private static JsonObject CopyValues(RecordData source)
    {
        // Yalnızca düzenlenebilir alanlar kopyalanır; durum, toplam ve kimlikler yeni kayıtta sunucuca üretilir.
        string[] editable =
        [
            "title", "description", "companyId", "contactId", "opportunityId", "priceListId", "ownerId", "currency", "reportingCurrency", "exchangeRate",
            "exchangeRateDate", "priceIncludesTax", "documentDiscountKind", "documentDiscountValue", "paymentTerms", "deliveryTerms", "commercialTerms",
            "internalNotes",
        ];
        var values = new JsonObject();
        foreach (var name in editable)
        {
            if (source.Values[name] is { } node)
            {
                values[name] = node.DeepClone();
            }
        }

        return values;
    }

    private static string Status(RecordData quote) => Text(quote, "status") ?? QuoteStatus.Draft;

    private static DateTimeOffset? ValidUntil(RecordData quote) =>
        Text(quote, "validUntil") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static decimal Number(RecordData quote, string name) =>
        quote.Values[name] is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : 0m;

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static RecordResult<RecordData> Invalid(string message, string field) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
