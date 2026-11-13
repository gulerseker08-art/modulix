using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sectors;
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
/// <param name="ApprovalReminderHours">Onay bu kadar saat beklerse adlı onaylayanlara bir kez hatırlatma e-postası; 0 = kapalı.</param>
/// <param name="ReplyReminderDays">CRM panosu "Benim günüm": sunulan teklif bu kadar gün yanıtsız kalınca hatırlatma önerilir; 0 = kapalı.</param>
public sealed record QuotePolicySettings(
    decimal? ApprovalDiscountPercent = null, decimal? ApprovalGrossAbove = null, int DefaultValidityDays = 30, bool AllowSelfApproval = false,
    bool OnAcceptWinOpportunity = false, int FollowUpDays = 0, int ExpiryReminderDays = 0, bool SyncOpportunityAmount = false, int ApprovalReminderHours = 0,
    int ReplyReminderDays = Reporting.MyDayRules.DefaultQuoteReminderDays);

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
    IQuotePolicyProvider? policyProvider = null,
    IActivityCodeCatalog? activityCodes = null,
    Modulix.Core.Contracts.Events.IBusinessEventPublisher? events = null) : IQuoteWorkflow
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

    public async Task<RecordResult<RecordData>> ApproveAsync(Guid id, long version, string? note, CancellationToken cancellationToken)
    {
        // Çok adımlı onay: bu onay bütün adımları tamamlıyorsa teklif onaylanır; tamamlamıyorsa yalnızca adım onayı tarihçeye yazılır, onay beklemeye devam eder.
        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var requirement = await RequirementAsync(loaded.Value!, cancellationToken);
        var given = new QuoteApprovalGiven(tenant.Current?.UserId ?? Guid.Empty, await OnBehalfOfAsync(cancellationToken));
        var completes = QuoteApprovalEvaluator.CompletesAll(requirement.Matches, await ApprovalsGivenAsync(id, cancellationToken), given);
        var current = QuoteApprovalEvaluator.Current(requirement.Steps);
        var stepLabel = current is null ? null : $"Adım {current.Step}/{requirement.Steps.Count}";
        var delegateLabel = requirement.ActingAsDelegate ? " (vekâleten)" : string.Empty;
        var text = Trim(note);
        if (completes)
        {
            return await Transition(id, version, QuoteAction.Approve, CrmModule.Permissions.QuotesApprove, text, (q, _) => new JsonObject
            {
                ["approvalStatus"] = QuoteApprovalStatus.Approved,
                ["statusReason"] = text,
            }, "Approved", cancellationToken, checkSelfApproval: true, checkApprover: true,
                historyReason: requirement.Steps.Count > 1 ? $"{stepLabel} onaylandı{delegateLabel}; onay tamamlandı.{(text is null ? string.Empty : $" {text}")}" : (text is null ? (delegateLabel.Length > 0 ? "Onaylandı (vekâleten)." : null) : $"{text}{delegateLabel}"));
        }

        return await Transition(id, version, QuoteAction.Approve, CrmModule.Permissions.QuotesApprove, text, (q, _) => new JsonObject(), "StepApproved",
            cancellationToken, checkSelfApproval: true, checkApprover: true,
            historyReason: $"{stepLabel} onaylandı{delegateLabel}.{(text is null ? string.Empty : $" {text}")}");
    }

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
            // Bağlı fırsatın hizmet kapsamı da o anda adlarıyla sabitlenir (ADR-0029 Dilim F3); fırsat/katalog sonradan değişse de belge aynı kalır.
            ["serviceScopeSnapshot"] = q.Values["serviceScopeToFreeze"]?.DeepClone(),
        }, "Presented", cancellationToken, enrichWithCompanyName: true, enrichWithServiceScope: true);

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
        CancellationToken cancellationToken, bool reasonRequired = false, bool checkSelfApproval = false, bool enrichWithCompanyName = false, bool checkApprover = false, string? historyReason = null,
        bool enrichWithServiceScope = false)
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

        if (enrichWithServiceScope)
        {
            quote.Values["serviceScopeToFreeze"] = await ServiceScopeToFreezeAsync(quote, cancellationToken);
        }

        var values = build(quote, now);
        RecordResult<RecordData> result;
        using (gate.Open([permission], eventKind, Trim(historyReason ?? reason)))
        {
            var command = new RecordCommand(Key, id, values, version) { Reason = $"quote.{eventKind}" };
            result = action == QuoteAction.Accept && events is not null
                // Kabul iş olayı (ADR-0039) durum değişikliğiyle aynı işlemde yazılır.
                ? await pipeline.RunAtomicAsync(async ct =>
                {
                    var updated = await pipeline.UpdateAsync(command, ct);
                    if (updated.IsSuccess)
                    {
                        await QuoteAcceptedEvent.PublishAsync(events, updated.Value!, QuoteAcceptedEvent.Manual, ct);
                    }

                    return updated;
                }, cancellationToken)
                : await pipeline.UpdateAsync(command, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Bağlı fırsatın hizmet kapsamı, faaliyet adlarıyla <c>[{"code","name"}]</c> dizisi olarak; fırsat yoksa ya da kapsamı boşsa null. Katalogda
    /// bulunamayan kodun adı yerine kodun kendisi yazılır (kayıt bozulmaz).
    /// </summary>
    private async Task<JsonNode?> ServiceScopeToFreezeAsync(RecordData quote, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Text(quote, "opportunityId"), out var opportunityId))
        {
            return null;
        }

        var codes = ActivityCodeList.Split(await reader.OpportunityServiceScopeAsync(opportunityId, cancellationToken));
        if (codes.Count == 0)
        {
            return null;
        }

        var names = activityCodes is null
            ? []
            : (await activityCodes.FindAsync(codes, cancellationToken)).ToDictionary(c => c.Code, c => c.Name, StringComparer.Ordinal);
        return new JsonArray([.. codes.Select(code => (JsonNode?)new JsonObject { ["code"] = code, ["name"] = names.GetValueOrDefault(code, code) })]);
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
        var required = threshold is not null || matches.Count > 0;
        var steps = QuoteApprovalEvaluator.Steps(matches, await ApprovalsGivenAsync(quote.Id, cancellationToken));
        var current = QuoteApprovalEvaluator.Current(steps);
        var onBehalfOf = await OnBehalfOfAsync(cancellationToken);
        var user = tenant.Current?.UserId;
        var canDecide = QuoteApprovalEvaluator.CanDecide(current, onBehalfOf);
        var actingAsDelegate = canDecide && current is { Approvers.Count: > 0 } && user is { } me && !current.Approvers.Contains(me);
        var restricted = steps.Any(s => s.Approvers.Count > 0);
        var note = !canDecide
            ? steps.Count > 1
                ? $"Bu teklifin onayı kural gereği belirli onaylayanlara atanmıştır; şu an {current!.Step}. adım ({steps.Count} adım) bekleniyor."
                : "Bu teklifin onayı kural gereği belirli onaylayanlara atanmıştır."
            : null;
        return new QuoteApprovalRequirement(required, matches, threshold, restricted, canDecide, note, steps, current?.Step ?? 0, actingAsDelegate);
    }

    /// <summary>Son gönderimden bu yana verilen adım onayları; her onay, onaylayan ve o an vekili olduğu kişiler adına sayılır.</summary>
    private async Task<List<QuoteApprovalGiven>> ApprovalsGivenAsync(Guid id, CancellationToken cancellationToken)
    {
        var events = await reader.StepApprovalsSinceSubmissionAsync(id, cancellationToken);
        if (events.Count == 0)
        {
            return [];
        }

        var delegations = await reader.ActiveDelegationsAsync(time.GetUtcNow(), cancellationToken);
        return [.. events.Where(e => e.ActorId is not null).Select(e => new QuoteApprovalGiven(e.ActorId!.Value,
            new HashSet<Guid>(delegations.Where(d => d.ToUserId == e.ActorId).Select(d => d.FromUserId)) { e.ActorId!.Value }))];
    }

    /// <summary>Geçerli kullanıcı + şu an vekili olduğu kişiler.</summary>
    private async Task<IReadOnlySet<Guid>> OnBehalfOfAsync(CancellationToken cancellationToken)
    {
        var set = new HashSet<Guid>();
        if (tenant.Current?.UserId is { } me && me != Guid.Empty)
        {
            set.Add(me);
            foreach (var d in await reader.ActiveDelegationsAsync(time.GetUtcNow(), cancellationToken))
            {
                if (d.ToUserId == me)
                {
                    set.Add(d.FromUserId);
                }
            }
        }

        return set;
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
