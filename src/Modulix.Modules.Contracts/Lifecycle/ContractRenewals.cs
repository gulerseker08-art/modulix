using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

public sealed record RenewalDecisionView(Guid Id, DateOnly PeriodEnd, string Outcome, DateOnly? NewEndDate, Guid? ResultContractId, string? Reason, string? Note, string Source, Guid? DecidedBy, DateTimeOffset At);

public sealed record ReminderView(Guid Id, DateOnly PeriodKey, string Kind, int ThresholdDays, string Status, Guid? ActivityId, string? Note, DateTimeOffset At);

/// <summary>Sözleşmenin dönem, yenileme ve fesih durumu: kalan günler, mevcut dönem kararı, karar geçmişi, hatırlatmalar ve otomatik uzatmanın neden uygulanmayacağı (varsa).</summary>
public sealed record RenewalOverview(
    Guid ContractId,
    string EffectiveStatus,
    string RenewalMode,
    int? RenewalPeriodMonths,
    int? NoticePeriodDays,
    DateOnly? EndDate,
    DateOnly? NoticeDeadline,
    bool IsIndefinite,
    int? DaysToNotice,
    int? DaysToEnd,
    DateOnly? TerminationDate,
    string? TerminationReason,
    bool AutoRenewalAllowedByTenant,
    string? AutoRenewalBlockedReason,
    IReadOnlyList<int> Thresholds,
    RenewalDecisionView? CurrentDecision,
    IReadOnlyList<RenewalDecisionView> History,
    IReadOnlyList<ReminderView> Reminders);

public sealed record RenewalDraftInput(DateOnly? StartDate, int? TermMonths);

public sealed record RenewalDraftResult(Guid ContractId, string Number, bool AlreadyExisted);

public sealed record TerminateInput(DateOnly EffectiveDate, string Reason, bool ConfirmShortNotice);

public sealed record RenewalCalendarItem(
    Guid ContractId, string Number, string Title, Guid PartyId, Guid? OwnerId, DateOnly? EndDate, DateOnly? NoticeDeadline, int? DaysToNotice, int? DaysToEnd, string RenewalMode,
    string EffectiveStatus, string? Decision, Guid? ResultContractId, DateOnly? TerminationDate, string? LastReminder);

public sealed record ChainItem(Guid Id, string Number, string Title, DateOnly? StartDate, DateOnly? EndDate, string EffectiveStatus, string SignatureStatus, string Relation);

/// <summary>
/// Sözleşme yenileme, yenilememe ve fesih (ADR-0022, dilim 8). Bir dönem (bitiş tarihi) için tek karar vardır: yenileme taslağı, otomatik uzatma ya da yenilenmeyecek; karar
/// (sözleşme, dönem) benzersiz indeksiyle korunur. Otomatik uzatma, ticari yeniden görüşme ve yeni sözleşme ayrı yollardır: yeniden görüşme/yeni sözleşme <b>taslak</b> açar ve normal onay,
/// imza sürecinden geçer (fiyat değişikliği bu yolla onaya tabidir); otomatik uzatma yalnızca açık politikayla aynı koşullarda dönemi uzatır. Fesih ayrı ve kritik bir yetkidir.
/// </summary>
public interface IContractRenewals
{
    Task<RecordResult<RenewalOverview>> GetAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>Yenileme taslağı: kaynak sözleşmenin müşteri, taraf ve kalemlerini kopyalayan yeni taslak sözleşme (önceki sözleşme bağıyla). Aynı dönem için tekrar çağrılırsa aynı taslak döner.</summary>
    Task<RecordResult<RenewalDraftResult>> CreateDraftAsync(Guid contractId, RenewalDraftInput input, CancellationToken cancellationToken);

    Task<RecordResult<RenewalOverview>> DeclineAsync(Guid contractId, string reason, string? note, CancellationToken cancellationToken);

    Task<RecordResult<RenewalOverview>> TerminateAsync(Guid contractId, TerminateInput input, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<RenewalCalendarItem>>> CalendarAsync(int days, CancellationToken cancellationToken);

    /// <summary>Yenileme zinciri: önceki sözleşmeler, bu sözleşme ve ardıllar (kullanıcının görebildikleri; görünmeyen halkada zincir durur).</summary>
    Task<RecordResult<IReadOnlyList<ChainItem>>> ChainAsync(Guid contractId, CancellationToken cancellationToken);
}

internal sealed class ContractRenewals(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    TimeProvider time,
    IContractPolicyProvider? policy = null) : IContractRenewals
{
    private const string Key = ContractsModule.Entities.Contract;

    private sealed record Loaded(RecordData Record, Contract Contract, ContractPolicySettings Policy, DateOnly Today);

    // ---- okuma

    public async Task<RecordResult<RenewalOverview>> GetAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<RenewalOverview>(contractId, null, cancellationToken);
        return loaded is null ? error! : RecordResult<RenewalOverview>.Ok(await OverviewAsync(loaded, cancellationToken));
    }

    public async Task<RecordResult<IReadOnlyList<ChainItem>>> ChainAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<IReadOnlyList<ChainItem>>(contractId, null, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        var items = new List<ChainItem>();
        // Önceki sözleşmeler: bağı izleyerek yukarı (en fazla 50 halka; döngü korumalı).
        var seen = new HashSet<Guid> { contractId };
        var predecessors = new List<ChainItem>();
        var cursor = loaded.Contract.PreviousContractId;
        while (cursor is { } previousId && predecessors.Count < 50 && seen.Add(previousId))
        {
            var previous = await pipeline.GetAsync(new RecordRef(Key, previousId), cancellationToken);
            if (!previous.IsSuccess || await reader.ContractAsync(previousId, cancellationToken) is not { } entity)
            {
                break;
            }

            predecessors.Add(Item(entity, "Predecessor"));
            cursor = entity.PreviousContractId;
        }

        predecessors.Reverse();
        items.AddRange(predecessors);
        items.Add(Item(loaded.Contract, "Current"));

        // Ardıllar: bu sözleşmeyi önceki sözleşme olarak gösterenler (görünür olanlar), genişlik öncelikli, en fazla 50.
        var queue = new Queue<Guid>([contractId]);
        while (queue.Count > 0 && items.Count < 100)
        {
            var parent = queue.Dequeue();
            var page = await pipeline.QueryAsync(new RecordQuery(Key, 1, RecordQuery.MaxPageSize, null, new Dictionary<string, string> { ["previousContractId"] = parent.ToString() }), cancellationToken);
            if (!page.IsSuccess)
            {
                break;
            }

            foreach (var row in page.Value!.Items.OrderBy(r => r.Values["startDate"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                if (seen.Add(row.Id) && await reader.ContractAsync(row.Id, cancellationToken) is { } successor)
                {
                    items.Add(Item(successor, "Successor"));
                    queue.Enqueue(row.Id);
                }
            }
        }

        return RecordResult<IReadOnlyList<ChainItem>>.Ok(items);
    }

    public async Task<RecordResult<IReadOnlyList<RenewalCalendarItem>>> CalendarAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 730);
        var today = await TodayAsync(cancellationToken);
        var horizon = today.AddDays(days);
        var rows = new List<RecordData>();
        for (var page = 1; page <= 10; page++)
        {
            var result = await pipeline.QueryAsync(
                new RecordQuery(Key, page, RecordQuery.MaxPageSize, null, new Dictionary<string, string> { ["effectiveStatus"] = EffectiveStatus.Active, ["endTo"] = horizon.AddDays(400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
                cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<IReadOnlyList<RenewalCalendarItem>>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }

            rows.AddRange(result.Value!.Items);
            if (result.Value.Items.Count < RecordQuery.MaxPageSize)
            {
                break;
            }
        }

        var ids = rows.Select(r => r.Id).ToList();
        var contracts = new List<Contract>();
        foreach (var id in ids)
        {
            // Aynı DbContext üzerinde sıralı okunur (paralel okuma desteklenmez).
            if (await reader.ContractAsync(id, cancellationToken) is { } found)
            {
                contracts.Add(found);
            }
        }
        var renewals = await reader.RenewalsOfAsync(ids, cancellationToken);
        var reminders = await reader.RemindersOfAsync(ids, cancellationToken);
        var items = new List<RenewalCalendarItem>();
        foreach (var c in contracts)
        {
            var target = ContractRenewalRules.Target(c.NoticeDeadline, c.EndDate, c.IsIndefinite);
            if (target is not { } due || due > horizon)
            {
                continue;
            }

            var decision = renewals.FirstOrDefault(r => r.ContractId == c.Id && r.PeriodEnd == c.EndDate);
            var last = reminders.Where(r => r.ContractId == c.Id && r.Kind == ReminderKind.Renewal && r.Status == ReminderStatus.Created).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            items.Add(new RenewalCalendarItem(
                c.Id, c.Number, c.Title, c.PartyId, c.OwnerId, c.EndDate, c.NoticeDeadline, c.NoticeDeadline is { } n ? n.DayNumber - today.DayNumber : null,
                c.EndDate is { } e ? e.DayNumber - today.DayNumber : null, c.RenewalMode, c.EffectiveStatus, decision?.Outcome, decision?.ResultContractId, c.TerminationDate,
                last is null ? null : $"{last.ThresholdDays} gün eşiği"));
        }

        return RecordResult<IReadOnlyList<RenewalCalendarItem>>.Ok([.. items.OrderBy(i => i.NoticeDeadline ?? i.EndDate).ThenBy(i => i.Number, StringComparer.Ordinal)]);
    }

    // ---- karar

    public async Task<RecordResult<RenewalOverview>> DeclineAsync(Guid contractId, string reason, string? note, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<RenewalOverview>(contractId, ContractsModule.Permissions.RenewalsManage, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        var c = loaded.Contract;
        if (!NonRenewalReason.All.Contains(reason))
        {
            return Invalid<RenewalOverview>("reason", "Yenilememe nedeni listeden seçilmelidir.");
        }

        if (Eligibility(c) is { } blocked)
        {
            return Conflict<RenewalOverview>(blocked);
        }

        var period = c.EndDate!.Value;
        if ((await reader.RenewalsAsync(contractId, cancellationToken)).FirstOrDefault(r => r.PeriodEnd == period) is { } existing)
        {
            return Conflict<RenewalOverview>($"Bu dönem için karar zaten verilmiş ({existing.Outcome}).");
        }

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var result = await CommitAsync(
            loaded, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.RenewalDeclined, $"{period:dd.MM.yyyy} dönemi yenilenmeyecek: {reason}{(cleanNote is null ? string.Empty : " — " + cleanNote)}", new JsonObject(),
            (ctx, contract, _) =>
            {
                ContractRenewalWriter.AddRenewal(ctx, contract.Id, period, RenewalOutcome.NotRenewed, "User", reason: reason, note: cleanNote);
                return Task.CompletedTask;
            },
            cancellationToken);
        return result.IsSuccess ? await GetAsync(contractId, cancellationToken) : As<RenewalOverview>(result);
    }

    public async Task<RecordResult<RenewalDraftResult>> CreateDraftAsync(Guid contractId, RenewalDraftInput input, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<RenewalDraftResult>(contractId, ContractsModule.Permissions.RenewalsManage, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        if (!granted.Contains(ContractsModule.Permissions.ContractsWrite) || !granted.Contains(ContractsModule.Permissions.FinancialsRead))
        {
            return RecordResult<RenewalDraftResult>.Fail(RecordErrorCode.PermissionDenied,
                $"Yenileme taslağı için '{ContractsModule.Permissions.ContractsWrite}' ve '{ContractsModule.Permissions.FinancialsRead}' izinleri gerekir (fiyatlar taslağa kopyalanır).");
        }

        var c = loaded.Contract;
        if (c.SignatureStatus != SignatureStatus.Completed || c.EffectiveStatus is not (EffectiveStatus.Active or EffectiveStatus.Suspended or EffectiveStatus.Expired))
        {
            return Conflict<RenewalDraftResult>("Yenileme taslağı yalnızca imzalı ve yürürlükteki (ya da yeni süresi dolmuş) sözleşme için açılır.");
        }

        if (c.IsIndefinite || c.EndDate is not { } period)
        {
            return Conflict<RenewalDraftResult>("Belirsiz süreli sözleşmenin yenilenecek bir dönemi yoktur.");
        }

        var existing = (await reader.RenewalsAsync(contractId, cancellationToken)).FirstOrDefault(r => r.PeriodEnd == period);
        if (existing is { Outcome: RenewalOutcome.DraftCreated, ResultContractId: { } draftId })
        {
            // Tekrarlanan istek aynı taslağı döndürür (mükerrer sözleşme üretilmez).
            var number = (await reader.ContractAsync(draftId, cancellationToken))?.Number ?? string.Empty;
            return RecordResult<RenewalDraftResult>.Ok(new RenewalDraftResult(draftId, number, true));
        }

        if (existing is not null)
        {
            return Conflict<RenewalDraftResult>($"Bu dönem için karar zaten verilmiş ({existing.Outcome}).");
        }

        var months = input.TermMonths ?? ContractRenewalRules.SuggestedMonths(c.RenewalPeriodMonths, c.StartDate, c.EndDate);
        var start = input.StartDate ?? ContractPeriod.NextTermStart(period);
        if (months is < 1 or > 120)
        {
            return Invalid<RenewalDraftResult>("termMonths", "Süre 1 ile 120 ay arasında olmalıdır.");
        }

        if (start <= period)
        {
            return Invalid<RenewalDraftResult>("startDate", "Yeni dönem, mevcut dönemin bitişinden sonra başlamalıdır.");
        }

        var end = ContractPeriod.TermEnd(start, months);
        var lines = await reader.LinesAsync(contractId, cancellationToken);
        var parties = await reader.PartiesAsync(contractId, cancellationToken);
        var values = new JsonObject
        {
            ["title"] = c.Title,
            ["type"] = c.Type,
            ["description"] = c.Description,
            ["partyId"] = c.PartyId.ToString(),
            ["personId"] = c.PersonId?.ToString(),
            ["ownerId"] = c.OwnerId?.ToString(),
            ["escalationUserId"] = c.EscalationUserId?.ToString(),
            ["previousContractId"] = c.Id.ToString(),
            ["startDate"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["isIndefinite"] = false,
            ["currency"] = c.Currency,
            ["priceIncludesTax"] = c.PriceIncludesTax,
            ["paymentTerms"] = c.PaymentTerms,
            ["renewalMode"] = c.RenewalMode,
            ["confidentiality"] = c.Confidentiality,
            ["hasNonStandardTerms"] = c.HasNonStandardTerms,
            ["riskLevel"] = c.RiskLevel,
        };
        if (c.RenewalPeriodMonths is { } renewalMonths) { values["renewalPeriodMonths"] = renewalMonths; }
        if (c.NoticePeriodDays is { } noticeDays) { values["noticePeriodDays"] = noticeDays; }
        if (c.ContractValue is { } contractValue) { values["contractValue"] = contractValue; }

        var reason = $"{c.Number} sözleşmesinin yenilemesi ({start:dd.MM.yyyy} – {end:dd.MM.yyyy})";
        var created = await pipeline.RunAtomicAsync<Guid>(async ct =>
        {
            RecordResult<RecordData> draft;
            using (gate.Open([ContractsModule.Permissions.ContractsWrite], ContractEventKinds.RenewalDraftCreated, reason))
            {
                draft = await pipeline.CreateAsync(new RecordCommand(Key, null, values) { Reason = "contract.RenewalDraftCreated" }, ct);
            }

            if (!draft.IsSuccess)
            {
                return RecordResult<Guid>.Fail(draft.Error!.Code, draft.Error.Message, draft.Error.FieldErrors);
            }

            foreach (var p in parties)
            {
                var added = await pipeline.CreateAsync(new RecordCommand(ContractsModule.Entities.ContractParty, null, new JsonObject
                {
                    ["contractId"] = draft.Value!.Id.ToString(),
                    ["role"] = p.Role,
                    ["partyId"] = p.PartyId?.ToString(),
                    ["personId"] = p.PersonId?.ToString(),
                    ["position"] = p.Position,
                    ["notes"] = p.Notes,
                }), ct);
                if (!added.IsSuccess)
                {
                    return RecordResult<Guid>.Fail(added.Error!.Code, added.Error.Message, added.Error.FieldErrors);
                }
            }

            foreach (var l in lines)
            {
                var added = await pipeline.CreateAsync(new RecordCommand(ContractsModule.Entities.ContractLine, null, new JsonObject
                {
                    ["contractId"] = draft.Value!.Id.ToString(),
                    ["position"] = l.Position,
                    ["kind"] = l.Kind,
                    ["productCode"] = l.ProductCode,
                    ["name"] = l.Name,
                    ["description"] = l.Description,
                    ["quantity"] = l.Quantity,
                    ["unitName"] = l.UnitName,
                    ["unitPrice"] = l.UnitPrice,
                    ["discountPercent"] = l.DiscountPercent,
                    ["taxRatePercent"] = l.TaxRatePercent,
                    ["billingPeriod"] = l.BillingPeriod,
                }), ct);
                if (!added.IsSuccess)
                {
                    return RecordResult<Guid>.Fail(added.Error!.Code, added.Error.Message, added.Error.FieldErrors);
                }
            }

            // Kaynak sözleşmede karar kaydı ve tarihçe (aynı işlem; (sözleşme, dönem) benzersizliği eşzamanlı çift taslağı engeller).
            var source = await pipeline.GetAsync(new RecordRef(Key, contractId), ct);
            if (!source.IsSuccess)
            {
                return RecordResult<Guid>.Fail(source.Error!.Code, source.Error.Message, source.Error.FieldErrors);
            }

            var draftId = draft.Value!.Id;
            var marked = await CommitAsync(
                loaded with { Record = source.Value! }, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.RenewalDraftCreated, reason, new JsonObject(),
                (ctx, contract, _) =>
                {
                    ContractRenewalWriter.AddRenewal(ctx, contract.Id, period, RenewalOutcome.DraftCreated, "User", resultContractId: draftId);
                    return Task.CompletedTask;
                },
                ct);
            return marked.IsSuccess ? RecordResult<Guid>.Ok(draftId) : RecordResult<Guid>.Fail(marked.Error!.Code, marked.Error.Message, marked.Error.FieldErrors);
        }, cancellationToken);

        if (!created.IsSuccess)
        {
            return RecordResult<RenewalDraftResult>.Fail(created.Error!.Code, created.Error.Message, created.Error.FieldErrors);
        }

        return RecordResult<RenewalDraftResult>.Ok(new RenewalDraftResult(created.Value, (await reader.ContractAsync(created.Value, cancellationToken))?.Number ?? string.Empty, false));
    }

    // ---- fesih

    public async Task<RecordResult<RenewalOverview>> TerminateAsync(Guid contractId, TerminateInput input, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<RenewalOverview>(contractId, ContractsModule.Permissions.ContractsTerminate, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        var c = loaded.Contract;
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            return Invalid<RenewalOverview>("reason", "Fesih gerekçesi zorunludur.");
        }

        var rule = ContractRenewalRules.Terminate(c.EffectiveStatus, c.SignatureStatus, input.EffectiveDate, loaded.Today, c.NoticePeriodDays);
        if (!rule.Allowed)
        {
            return Conflict<RenewalOverview>(rule.Reason!);
        }

        if (c.StartDate is { } start && input.EffectiveDate < start)
        {
            return Invalid<RenewalOverview>("effectiveDate", "Fesih tarihi sözleşme başlangıcından önce olamaz.");
        }

        if (!c.IsIndefinite && c.EndDate is { } end && input.EffectiveDate > end)
        {
            return Invalid<RenewalOverview>("effectiveDate", "Fesih tarihi sözleşme bitişinden sonra olamaz.");
        }

        if (rule.ShortNotice && !input.ConfirmShortNotice)
        {
            return Conflict<RenewalOverview>($"Fesih tarihi bildirim süresinin ({c.NoticePeriodDays} gün) altında; yine de feshetmek için kısa bildirimi onaylayın.");
        }

        var reason = input.Reason.Trim();
        var values = new JsonObject
        {
            ["terminationDate"] = input.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["terminationReason"] = reason,
            ["statusReason"] = reason,
        };
        if (rule.Immediate)
        {
            values["effectiveStatus"] = EffectiveStatus.Terminated;
        }

        var kind = rule.Immediate ? ContractEventKinds.Terminated : ContractEventKinds.TerminationScheduled;
        var result = await CommitAsync(
            loaded, [ContractsModule.Permissions.ContractsTerminate], kind, $"{input.EffectiveDate:dd.MM.yyyy}: {reason}", values,
            (ctx, contract, ct) => rule.Immediate ? ContractRenewalWriter.CancelCommitmentsAsync(ctx, contract.Id, "Sözleşme feshedildi.", ct) : Task.CompletedTask, cancellationToken);
        return result.IsSuccess ? await GetAsync(contractId, cancellationToken) : As<RenewalOverview>(result);
    }

    // ---- yardımcılar

    private static string? Eligibility(Contract c) =>
        c.SignatureStatus != SignatureStatus.Completed || !EffectiveStatus.IsBinding(c.EffectiveStatus)
            ? "Yenileme kararı yalnızca imzalı ve yürürlükteki sözleşme için verilir."
            : c.IsIndefinite || c.EndDate is null ? "Belirsiz süreli sözleşmenin yenilenecek bir dönemi yoktur." : null;

    private async Task<RenewalOverview> OverviewAsync(Loaded loaded, CancellationToken cancellationToken)
    {
        var c = loaded.Contract;
        var renewals = await reader.RenewalsAsync(c.Id, cancellationToken);
        var reminders = await reader.RemindersAsync(c.Id, cancellationToken);
        var current = renewals.FirstOrDefault(r => r.PeriodEnd == c.EndDate);
        // Otomatik uzatmanın neden uygulanmayacağı (varsa): yöntem, ayar ve dönem koşulları.
        var blocked = c.RenewalMode != RenewalMode.Automatic ? "Yenileme yöntemi otomatik değil; yenileme taslağı ile yapılır."
            : !loaded.Policy.AllowAutoRenewal ? "Firma ayarı otomatik uzatmaya izin vermiyor; sözleşme kendiliğinden uzatılmaz."
            : c.RenewalPeriodMonths is not > 0 ? "Yenileme süresi tanımlı değil."
            : current is not null ? "Bu dönem için karar zaten var."
            : null;
        return new RenewalOverview(
            c.Id, c.EffectiveStatus, c.RenewalMode, c.RenewalPeriodMonths, c.NoticePeriodDays, c.EndDate, c.NoticeDeadline, c.IsIndefinite,
            c.NoticeDeadline is { } n ? n.DayNumber - loaded.Today.DayNumber : null, c.EndDate is { } e ? e.DayNumber - loaded.Today.DayNumber : null, c.TerminationDate, c.TerminationReason,
            loaded.Policy.AllowAutoRenewal, blocked,
            ContractRenewalRules.ParseThresholds(loaded.Policy.RenewalReminderDays), current is null ? null : Decision(current), [.. renewals.Select(Decision)], [.. reminders.Select(Reminder)]);
    }

    private static RenewalDecisionView Decision(ContractRenewal r) => new(r.Id, r.PeriodEnd, r.Outcome, r.NewEndDate, r.ResultContractId, r.Reason, r.Note, r.Source, r.DecidedBy, r.CreatedAt);

    private static ReminderView Reminder(ContractReminder r) => new(r.Id, r.PeriodKey, r.Kind, r.ThresholdDays, r.Status, r.ActivityId, r.Note, r.CreatedAt);

    private static ChainItem Item(Contract c, string relation) => new(c.Id, c.Number, c.Title, c.StartDate, c.EndDate, c.EffectiveStatus, c.SignatureStatus, relation);

    private async Task<(Loaded? Loaded, RecordResult<T>? Error)> LoadAsync<T>(Guid contractId, string? permission, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, contractId), cancellationToken);
        if (!record.IsSuccess)
        {
            return (null, RecordResult<T>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors));
        }

        if (permission is not null && !(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir."));
        }

        var contract = await reader.ContractAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Sözleşme bulunamadı."));
        }

        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        return (new Loaded(record.Value!, contract, settings, ContractPeriod.Today(time.GetUtcNow(), Zone(settings))), null);
    }

    private async Task<RecordResult<RecordData>> CommitAsync(
        Loaded loaded, IReadOnlyList<string> permissions, string eventKind, string? reason, JsonObject values, Func<ContractsStoreContext, Contract, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        using (gate.Open(permissions, eventKind, reason, effect))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, loaded.Contract.Id, values, loaded.Record.Version) { Reason = $"contract.{eventKind}" }, cancellationToken);
        }
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        ContractPeriod.Today(time.GetUtcNow(), Zone(policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken)));

    private static TimeZoneInfo Zone(ContractPolicySettings settings) => TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    private static RecordResult<T> Conflict<T>(string message) => RecordResult<T>.Fail(RecordErrorCode.Conflict, message, [new RecordFieldError("status", message)]);

    private static RecordResult<T> Invalid<T>(string field, string message) => RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    private static RecordResult<T> As<T>(RecordResult<RecordData> result) => RecordResult<T>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
}
