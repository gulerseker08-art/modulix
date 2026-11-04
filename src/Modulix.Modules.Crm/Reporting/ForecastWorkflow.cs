using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Reporting;

public sealed record CreateForecastRequest(PeriodKind Kind, DateOnly? Anchor, string? ScopeType, Guid? ScopeUserId, string? Currency);

/// <summary>Tahmin başlığı. Alan adları Shared'daki <c>ForecastHeaderDto</c> ile birebir aynıdır.</summary>
public sealed record ForecastHeader(
    Guid Id, long Version, string PeriodKind, DateOnly PeriodStart, DateOnly PeriodEnd, string ScopeType, Guid? ScopeUserId, string ReportingCurrency,
    int VersionNo, string Status, Guid? PreviousSubmissionId, string? Note, DateTimeOffset? SubmittedAt, Guid? SubmittedBy, DateTimeOffset? LockedAt,
    Guid? LockedBy, string? ReopenReason, string? ReturnReason, decimal? RealizedAtLock, bool RealizedPartial, DateTimeOffset? RefreshedAt, DateTimeOffset? ChangedAt);

public sealed record ForecastItemView(
    Guid Id, Guid OpportunityId, string OpportunityNumber, string Title, Guid? OwnerId, Guid? CurrentOwnerId, string Stage, decimal? SourceAmount,
    string SourceCurrency, int? Probability, string ProbabilitySource, DateOnly? ExpectedCloseDate, decimal? RateUsed, DateOnly? RateDate, bool RateMissing,
    decimal? BaseAmount, string BaseCategory, string Category, decimal? AdjustmentAmount, decimal? FinalAmount, int? ProbabilityOverride,
    int? EffectiveProbability, decimal? Weighted, IReadOnlyList<string> Flags);

public sealed record ForecastAdjustmentView(
    Guid Id, Guid? ItemId, Guid OpportunityId, string Kind, string? FromValue, string? ToValue, string Reason, Guid? ChangedBy, DateTimeOffset At);

public sealed record ForecastCategoryTotal(string Category, int Count, decimal Amount, decimal Weighted);

public sealed record ForecastCapabilities(bool CanWrite, bool CanSubmit, bool CanReturn, bool CanLock, bool CanReopen, bool CanDelete);

/// <summary>Doğruluk: kilitli tahminin dönem bitince gerçekleşenle karşılaştırması. <see cref="State"/>: NotLocked, Pending (dönem sürüyor), Ready.</summary>
public sealed record ForecastAccuracy(string State, int? HorizonDays, ForecastVariance? CommitScenario, ForecastVariance? WeightedScenario, decimal? Actual);

/// <summary>Tahmin ayrıntısı: yalnızca çağıranın <b>bugünkü</b> erişim yetkisindeki satırlar ve onlardan hesaplanan toplamlar (kaydedilmiş toplam yoktur).</summary>
public sealed record ForecastDetail(
    ForecastHeader Header,
    bool ItemsVisible,
    bool AmountsVisible,
    IReadOnlyList<ForecastItemView> Items,
    IReadOnlyList<ForecastAdjustmentView> Adjustments,
    IReadOnlyList<ForecastCategoryTotal> ByCategory,
    ForecastScenarios? Scenarios,
    decimal? Realized,
    bool RealizedLive,
    ForecastAccuracy Accuracy,
    ForecastCapabilities Capabilities,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions);

public sealed record ForecastCompareEntry(Guid OpportunityId, string OpportunityNumber, string Title, decimal? BeforeAmount, decimal? AfterAmount, string? BeforeCategory, string? AfterCategory, string? Outcome);

/// <summary>Sürüm karşılaştırması: <see cref="Previous"/> → <see cref="Current"/>. Kaybolan satırın <c>Outcome</c>ı: Won, Lost, Cancelled, OnHold, Postponed (tarih dönem dışına kaydı), Deleted, Other.</summary>
public sealed record ForecastComparison(
    Guid CurrentId, Guid PreviousId, int CurrentVersionNo, int PreviousVersionNo, bool AmountsVisible,
    IReadOnlyList<ForecastCompareEntry> Added, IReadOnlyList<ForecastCompareEntry> Removed, IReadOnlyList<ForecastCompareEntry> AmountChanged,
    IReadOnlyList<ForecastCompareEntry> CategoryChanged);

public interface IForecastWorkflow
{
    Task<RecordResult<IReadOnlyList<ForecastHeader>>> ListAsync(DateOnly? from, DateOnly? to, string? status, CancellationToken cancellationToken);

    Task<RecordResult<ForecastDetail>> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Dönem, kapsam ve para birimi için Taslak tahmin açar: kapsamdaki açık fırsatların anlık görüntüsünü alır.</summary>
    Task<RecordResult<ForecastDetail>> CreateAsync(CreateForecastRequest request, CancellationToken cancellationToken);

    /// <summary>Taslağın fırsat anlık görüntüsünü yeniler; yönetici düzeltmeleri korunur.</summary>
    Task<RecordResult<ForecastDetail>> RefreshAsync(Guid id, long? version, CancellationToken cancellationToken);

    /// <summary>Taslakta bir satırın kategori, tutar (delta) ya da olasılık düzeltmesi; gerekçe zorunludur, kaynak fırsat değişmez.</summary>
    Task<RecordResult<ForecastDetail>> AdjustAsync(Guid id, Guid itemId, string? kind, string? value, string? reason, long? version, CancellationToken cancellationToken);

    Task<RecordResult<ForecastDetail>> SetNoteAsync(Guid id, string? note, long? version, CancellationToken cancellationToken);

    Task<RecordResult<ForecastDetail>> SubmitAsync(Guid id, long? version, CancellationToken cancellationToken);

    Task<RecordResult<ForecastDetail>> ReturnToDraftAsync(Guid id, string? reason, long? version, CancellationToken cancellationToken);

    Task<RecordResult<ForecastDetail>> LockAsync(Guid id, long? version, CancellationToken cancellationToken);

    /// <summary>Kilitli sürümü gerekçeyle yeniden açar: <b>yeni sürüm</b> (Taslak) üretir; önceki kilitli sürüm değişmez.</summary>
    Task<RecordResult<ForecastDetail>> ReopenAsync(Guid id, string? reason, CancellationToken cancellationToken);

    Task<RecordResult<bool>> DeleteDraftAsync(Guid id, long? version, CancellationToken cancellationToken);

    Task<RecordResult<ForecastComparison>> CompareAsync(Guid id, Guid? againstId, CancellationToken cancellationToken);
}

/// <summary>
/// Sürümlü satış tahmini (ADR-0026). Tahmin, dönem × kapsam × para birimi için kapsamdaki <b>açık fırsatların anlık görüntüsü</b> ve yönetici düzeltmeleridir; Taslak → Sunuldu → Kilitlendi
/// akışıyla yönetilir. Bütün yazmalar kayıt boru hattından (izin, audit, olay) ve <see cref="ForecastWorkflowGate"/> ile aynı işlemde geçer. Tahmin kategorileri birbirini dışlar;
/// ağırlıklı tahmin kategorilerden ayrı bir yöntemdir. Düzeltme kaynak fırsatı değiştirmez. Kilitli sürüm değişmez; yeniden açma gerekçelidir ve yeni sürüm üretir.
/// Tenant kapsamlı tahmin ve kilit, yalnızca tüm kayıtlara erişen (veri kapsamı Tümü) kullanıcıyla hazırlanır; böylece anlık görüntü eksik kayıtla oluşmaz.
/// </summary>
internal sealed class ForecastWorkflow(
    IRecordPipeline pipeline, IRecordAccess recordAccess, ICurrentAccess access, SalesPlanningData planning, SalesOpportunityReader reader,
    ForecastWorkflowGate gate, ITenantContextAccessor tenant, ITenantMembers members, TimeProvider time,
    ISalesReportingPolicyProvider? policyProvider = null) : IForecastWorkflow
{
    private const string Key = CrmModule.Entities.ForecastSubmission;

    public static IReadOnlyList<string> Definitions { get; } =
    [
        "Tahmin, dönem × kapsam × para birimi için kapsamdaki açık fırsatların anlık görüntüsüdür; kaynak fırsat tutarı hiçbir zaman değişmez. Kur, anlık görüntü gününün kurudur ve satırda saklanır.",
        "Kategoriler birbirini dışlar: Hariç, Pipeline, En İyi Durum, Taahhüt. Kazanıldı kategori değil gerçek sonuçtur. Varsayılan kategori olasılık eşiklerinden gelir (ayarlanabilir); yönetici gerekçeyle değiştirir.",
        "Senaryolar: Taahhüt = gerçekleşen + açık taahhüt; İyimser = gerçekleşen + taahhüt + en iyi durum; Ağırlıklı = gerçekleşen + Σ(nihai tutar × etkin olasılık / 100). Kategoriler ve ağırlıklı tahmin birbirinin üstüne eklenmez; Hariç satırlar hiçbirine girmez.",
        "Nihai tutar = temel tutar + yönetici düzeltmesi (delta). Etkin olasılık = yönetici olasılık düzeltmesi varsa o, yoksa fırsatın olasılığı. Olasılığı bilinmeyen satır ağırlıklı toplamda sıfır sayılmaz; veri eksiği olarak işaretlenir ve ağırlıklı toplama girmez.",
        "Kur bulunamayan satırın temel tutarı yoktur ve hiçbir toplama girmez (1 varsayılmaz); satır işaretlenir.",
        "Taslak → Sunuldu → Kilitlendi. Kilitli sürüm değişmez; yeniden açma gerekçelidir ve yeni sürüm (Taslak) üretir, önceki kilitli sürüm korunur. Kilit anındaki gerçekleşen satış sürümde saklanır.",
        "Doğruluk: kilitli tahminin senaryo toplamı, dönem bittikten sonra gerçekleşen satışla karşılaştırılır (hata = tahmin − gerçekleşen; gerçekleşen sıfırsa yüzde hata yoktur). Karşılaştırma, tahminin kilitlendiği güne olan ufuk (gün) ile birlikte okunmalıdır; son gün kilitlenmiş tahmin, ay başı tahmin gibi puanlanmaz.",
        "Satırlar ve toplamlar yalnızca bugünkü erişim yetkinize göre gösterilir (fırsatın bugünkü sorumlusu). Satırdaki sorumlu, anlık görüntü anındaki (tarihsel) sorumludur; güncel sorumlu ayrıca verilir.",
        "Dönem, tenant saat dilimi ve mali takvimle belirlenir; başlangıç dahil, sonraki dönem başlangıcı hariçtir.",
    ];

    // ---- Okuma

    public async Task<RecordResult<IReadOnlyList<ForecastHeader>>> ListAsync(DateOnly? from, DateOnly? to, string? status, CancellationToken cancellationToken)
    {
        var filters = new Dictionary<string, string>();
        if (from is { } f)
        {
            filters["from"] = SalesOpportunityReader.Iso(f);
        }

        if (to is { } t)
        {
            filters["to"] = SalesOpportunityReader.Iso(t);
        }

        if (!string.IsNullOrEmpty(status))
        {
            filters["status"] = status;
        }

        var page = await pipeline.QueryAsync(new RecordQuery(Key, 1, RecordQuery.MaxPageSize, null, filters), cancellationToken);
        return page.IsSuccess
            ? RecordResult<IReadOnlyList<ForecastHeader>>.Ok([.. page.Value!.Items.Select(ToHeader)])
            : RecordResult<IReadOnlyList<ForecastHeader>>.Fail(page.Error!.Code, page.Error.Message, page.Error.FieldErrors);
    }

    public async Task<RecordResult<ForecastDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        return record.IsSuccess ? RecordResult<ForecastDetail>.Ok(await DetailAsync(ToHeader(record.Value!), cancellationToken)) : Forward<ForecastDetail>(record.Error!);
    }

    // ---- Yazma

    public async Task<RecordResult<ForecastDetail>> CreateAsync(CreateForecastRequest request, CancellationToken cancellationToken)
    {
        var policy = await PolicyAsync(cancellationToken);
        var scopeType = request.ScopeType ?? TargetScope.Tenant;
        if (!TargetScope.All.Contains(scopeType))
        {
            return Invalid("scopeType", $"Şunlardan biri olmalıdır: {string.Join(", ", TargetScope.All)}.");
        }

        if (scopeType == TargetScope.User && (request.ScopeUserId is not { } user || !await members.IsActiveMemberAsync(user, cancellationToken)))
        {
            return Invalid("scopeUserId", "Kullanıcı tahmininde kullanıcı zorunludur ve bu kuruluşun etkin üyesi olmalıdır.");
        }

        if (scopeType == TargetScope.Tenant && request.ScopeUserId is not null)
        {
            return Invalid("scopeUserId", "Tenant tahmininde kullanıcı verilmez.");
        }

        var currency = (request.Currency ?? policy.DefaultCurrency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            return Invalid("currency", "Üç harfli para birimi kodu girin (ör. TRY).");
        }

        var today = ReportPeriods.Today(time.GetUtcNow(), policy.TimeZone);
        var period = ReportPeriods.Containing(request.Kind, request.Anchor ?? today, policy.FiscalStartMonth);
        var scopeUserId = scopeType == TargetScope.User ? request.ScopeUserId : null;

        var guard = await GuardAsync(scopeType, scopeUserId, requireAmounts: true, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        if (await planning.SlotExistsAsync(period.Start, period.LastDay, scopeType, scopeUserId, currency, cancellationToken))
        {
            return Conflict("Bu dönem, kapsam ve para birimi için tahmin zaten var; kilitliyse gerekçeyle yeniden açın.");
        }

        var rows = await ReadOpenAsync(period, scopeUserId, cancellationToken);
        if (rows.Error is not null)
        {
            return Forward<ForecastDetail>(rows.Error);
        }

        var rates = await RatesAsync(cancellationToken);
        var now = time.GetUtcNow();
        var items = rows.Rows.Select(r => NewItem(Guid.Empty, r, rates, currency, today, policy.Forecast)).ToList();

        RecordResult<RecordData> result;
        using (gate.Open([CrmModule.Permissions.ForecastsWrite], (sub, store, _) =>
        {
            sub.PeriodKind = period.Kind.ToString();
            sub.PeriodStart = period.Start;
            sub.PeriodEnd = period.LastDay;
            sub.ScopeType = scopeType;
            sub.ScopeUserId = scopeUserId;
            sub.ReportingCurrency = currency;
            sub.VersionNo = 1;
            sub.Status = ForecastStatus.Draft;
            sub.RefreshedAt = now;
            sub.ChangedAt = now;
            foreach (var item in items)
            {
                item.SubmissionId = sub.Id;
                store.Add(item);
            }

            return Task.CompletedTask;
        }))
        {
            result = await pipeline.CreateAsync(new RecordCommand(Key, null, new JsonObject { ["changedAt"] = Iso(now) }) { Reason = "crm.forecast.created" }, cancellationToken);
        }

        if (!result.IsSuccess)
        {
            return Forward<ForecastDetail>(result.Error!);
        }

        var created = result.Value!.Id;
        var warnings = rows.Truncated ? $"Okuma sınırına ({SalesOpportunityReader.MaxRows}) ulaşıldı; anlık görüntü eksik olabilir." : null;
        var detail = await GetAsync(created, cancellationToken);
        return warnings is null || !detail.IsSuccess
            ? detail
            : RecordResult<ForecastDetail>.Ok(detail.Value! with { Warnings = [warnings, .. detail.Value.Warnings] });
    }

    public async Task<RecordResult<ForecastDetail>> RefreshAsync(Guid id, long? version, CancellationToken cancellationToken)
    {
        var header = await HeaderAsync(id, cancellationToken);
        if (header.Error is not null)
        {
            return Forward<ForecastDetail>(header.Error);
        }

        var h = header.Value!;
        if (h.Status != ForecastStatus.Draft)
        {
            return Conflict("Yalnızca taslak tahmin yenilenir.");
        }

        var guard = await GuardAsync(h.ScopeType, h.ScopeUserId, requireAmounts: true, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        var policy = await PolicyAsync(cancellationToken);
        var today = ReportPeriods.Today(time.GetUtcNow(), policy.TimeZone);
        var period = ReportPeriods.Containing(Enum.Parse<PeriodKind>(h.PeriodKind), h.PeriodStart, policy.FiscalStartMonth);
        var rows = await ReadOpenAsync(new ReportPeriod(period.Kind, h.PeriodStart, h.PeriodEnd.AddDays(1), period.Label), h.ScopeUserId, cancellationToken);
        if (rows.Error is not null)
        {
            return Forward<ForecastDetail>(rows.Error);
        }

        var rates = await RatesAsync(cancellationToken);
        var now = time.GetUtcNow();
        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsWrite], "refreshed", async (sub, store, ct) =>
        {
            EnsureDraft(sub);
            var existing = await store.TrackedItemsAsync(sub.Id, ct);
            var byOpportunity = existing.ToDictionary(i => i.OpportunityId);
            foreach (var row in rows.Rows)
            {
                if (byOpportunity.TryGetValue(row.Id, out var item))
                {
                    var oldBase = item.BaseCategory;
                    Snapshot(item, row, rates, h.ReportingCurrency, today, policy.Forecast);
                    // Yönetici kategoriyi elle değiştirmediyse yeni varsayılana geçer; değiştirdiyse düzeltme korunur.
                    if (item.Category == oldBase)
                    {
                        item.Category = item.BaseCategory;
                    }

                    item.Touch();
                }
                else
                {
                    store.Add(NewItem(sub.Id, row, rates, h.ReportingCurrency, today, policy.Forecast));
                }
            }

            var live = rows.Rows.Select(r => r.Id).ToHashSet();
            store.Remove(existing.Where(i => !live.Contains(i.OpportunityId)));
            sub.RefreshedAt = now;
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> AdjustAsync(
        Guid id, Guid itemId, string? kind, string? value, string? reason, long? version, CancellationToken cancellationToken)
    {
        if (kind is null || !ForecastAdjustmentKind.All.Contains(kind))
        {
            return Invalid("kind", $"Şunlardan biri olmalıdır: {string.Join(", ", ForecastAdjustmentKind.All)}.");
        }

        if (ForecastRules.ValidateReason(reason) is { } reasonError)
        {
            return Invalid("reason", reasonError);
        }

        var header = await HeaderAsync(id, cancellationToken);
        if (header.Error is not null)
        {
            return Forward<ForecastDetail>(header.Error);
        }

        var h = header.Value!;
        var guard = await GuardAsync(h.ScopeType, h.ScopeUserId, requireAmounts: true, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        var current = await planning.FindItemAsync(itemId, id, cancellationToken);
        if (current is null || !await IsVisibleAsync(current.OpportunityId, cancellationToken))
        {
            return RecordResult<ForecastDetail>.Fail(RecordErrorCode.NotFound, "Tahmin satırı bulunamadı.");
        }

        var text = reason!.Trim();
        var user = tenant.Current?.UserId;
        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsWrite], "adjusted", async (sub, store, ct) =>
        {
            EnsureDraft(sub);
            var item = await store.TrackedItemAsync(itemId, sub.Id, ct);
            string? from;
            string? to;
            switch (kind)
            {
                case ForecastAdjustmentKind.Category:
                    if (value is null || !ForecastCategory.All.Contains(value))
                    {
                        throw Fail("value", $"Kategori şunlardan biri olmalıdır: {string.Join(", ", ForecastCategory.All)}.");
                    }

                    if (item.Category == value)
                    {
                        throw new RecordConflictException("Kategori zaten bu değerde.");
                    }

                    (from, to, item.Category) = (item.Category, value, value);
                    break;

                case ForecastAdjustmentKind.Amount:
                    if (item.BaseAmount is null)
                    {
                        throw Fail("value", "Tutarı ya da kuru olmayan satırın tutarı düzeltilemez; önce fırsat tutarını/kuru tamamlayıp yenileyin.");
                    }

                    var delta = ParseDecimal(value);
                    if (delta is null && !string.IsNullOrWhiteSpace(value))
                    {
                        throw Fail("value", "Düzeltme, en fazla iki ondalıklı bir sayı (delta) olmalıdır; boş bırakmak düzeltmeyi kaldırır.");
                    }

                    if (delta is { } shift && (Math.Abs(shift) > 999_999_999_999m || item.BaseAmount + shift < 0))
                    {
                        throw Fail("value", "Düzeltme sonrası nihai tutar negatif olamaz.");
                    }

                    var next = delta is null or 0m ? null : delta;
                    (from, to, item.AdjustmentAmount) = (Format(item.AdjustmentAmount), Format(next), next);
                    break;

                default:
                    int? percent = string.IsNullOrWhiteSpace(value) ? null : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : -1;
                    if (percent is < 0 or > 100)
                    {
                        throw Fail("value", "Olasılık 0-100 arası tam sayı olmalıdır; boş bırakmak düzeltmeyi kaldırır.");
                    }

                    (from, to, item.ProbabilityOverride) = (item.ProbabilityOverride?.ToString(CultureInfo.InvariantCulture), percent?.ToString(CultureInfo.InvariantCulture), percent);
                    break;
            }

            item.Touch();
            store.Add(new ForecastAdjustment
            {
                SubmissionId = sub.Id, ItemId = item.Id, OpportunityId = item.OpportunityId, Kind = kind, FromValue = from, ToValue = to, Reason = text, ChangedBy = user,
            });
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> SetNoteAsync(Guid id, string? note, long? version, CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is { Length: > ForecastSubmission.NoteMaxLength })
        {
            return Invalid("note", $"Not en fazla {ForecastSubmission.NoteMaxLength} karakter olabilir.");
        }

        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsWrite], "noted", (sub, _, _) =>
        {
            EnsureDraft(sub);
            sub.Note = text;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> SubmitAsync(Guid id, long? version, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var user = tenant.Current?.UserId;
        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsSubmit], "submitted", (sub, _, _) =>
        {
            Transition(sub, ForecastStatus.Submitted);
            sub.SubmittedAt = now;
            sub.SubmittedBy = user;
            sub.ReturnReason = null;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> ReturnToDraftAsync(Guid id, string? reason, long? version, CancellationToken cancellationToken)
    {
        if (ForecastRules.ValidateReason(reason) is { } error)
        {
            return Invalid("reason", error);
        }

        var text = reason!.Trim();
        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsSubmit], "returned", (sub, _, _) =>
        {
            Transition(sub, ForecastStatus.Draft);
            sub.SubmittedAt = null;
            sub.SubmittedBy = null;
            sub.ReturnReason = text;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> LockAsync(Guid id, long? version, CancellationToken cancellationToken)
    {
        var header = await HeaderAsync(id, cancellationToken);
        if (header.Error is not null)
        {
            return Forward<ForecastDetail>(header.Error);
        }

        var h = header.Value!;
        var guard = await GuardAsync(h.ScopeType, h.ScopeUserId, requireAmounts: true, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        // Kilit anındaki gerçekleşen satış: dönemde kazanılmış fırsatlar (kazanım gününün kuruyla), kapsamdaki kullanıcıya göre.
        var policy = await PolicyAsync(cancellationToken);
        var today = ReportPeriods.Today(time.GetUtcNow(), policy.TimeZone);
        var period = new ReportPeriod(Enum.Parse<PeriodKind>(h.PeriodKind), h.PeriodStart, h.PeriodEnd.AddDays(1), string.Empty);
        var realized = await RealizedAsync(period, h.ScopeUserId, h.ReportingCurrency, today, cancellationToken);
        if (realized.Error is not null)
        {
            return Forward<ForecastDetail>(realized.Error);
        }

        var now = time.GetUtcNow();
        var user = tenant.Current?.UserId;
        return await WriteAsync(id, version, [CrmModule.Permissions.ForecastsLock], "locked", (sub, _, _) =>
        {
            Transition(sub, ForecastStatus.Locked);
            sub.LockedAt = now;
            sub.LockedBy = user;
            sub.RealizedAtLock = realized.Total!.Amount;
            sub.RealizedPartial = realized.Total.IsPartial || realized.Truncated;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<RecordResult<ForecastDetail>> ReopenAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        if (ForecastRules.ValidateReason(reason) is { } error)
        {
            return Invalid("reason", error);
        }

        var header = await HeaderAsync(id, cancellationToken);
        if (header.Error is not null)
        {
            return Forward<ForecastDetail>(header.Error);
        }

        var source = header.Value!;
        if (source.Status != ForecastStatus.Locked)
        {
            return Conflict("Yalnızca kilitli tahmin yeniden açılır.");
        }

        if (!await access.HasPermissionAsync(CrmModule.Permissions.ForecastsReopen, cancellationToken))
        {
            return RecordResult<ForecastDetail>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{CrmModule.Permissions.ForecastsReopen}' izni gerekir.");
        }

        var guard = await GuardAsync(source.ScopeType, source.ScopeUserId, requireAmounts: true, cancellationToken);
        if (guard is not null)
        {
            return guard;
        }

        var siblings = await planning.VersionsAsync(source.PeriodStart, source.PeriodEnd, source.ScopeType, source.ScopeUserId, source.ReportingCurrency, cancellationToken);
        if (siblings.Any(s => s.Status != ForecastStatus.Locked))
        {
            return Conflict("Bu dönem için zaten açık bir sürüm var; önce onu kilitleyin ya da silin.");
        }

        var text = reason!.Trim();
        var nextVersion = siblings.Max(s => s.VersionNo) + 1;
        var now = time.GetUtcNow();
        RecordResult<RecordData> result;
        using (gate.Open([CrmModule.Permissions.ForecastsReopen], async (sub, store, ct) =>
        {
            sub.PeriodKind = source.PeriodKind;
            sub.PeriodStart = source.PeriodStart;
            sub.PeriodEnd = source.PeriodEnd;
            sub.ScopeType = source.ScopeType;
            sub.ScopeUserId = source.ScopeUserId;
            sub.ReportingCurrency = source.ReportingCurrency;
            sub.VersionNo = nextVersion;
            sub.Status = ForecastStatus.Draft;
            sub.PreviousSubmissionId = source.Id;
            sub.ReopenReason = text;
            sub.RefreshedAt = now;
            sub.ChangedAt = now;

            // Satırlar (düzeltmeleriyle birlikte) yeni sürüme kopyalanır; kilitli sürümün satırları ve düzeltme geçmişi olduğu gibi kalır.
            var items = await planning.ItemsAsync(source.Id, ct);
            foreach (var item in items)
            {
                store.Add(Copy(item, sub.Id));
            }
        }))
        {
            result = await pipeline.CreateAsync(new RecordCommand(Key, null, new JsonObject { ["changedAt"] = Iso(now) }) { Reason = "crm.forecast.reopened" }, cancellationToken);
        }

        return result.IsSuccess ? await GetAsync(result.Value!.Id, cancellationToken) : Forward<ForecastDetail>(result.Error!);
    }

    public async Task<RecordResult<bool>> DeleteDraftAsync(Guid id, long? version, CancellationToken cancellationToken)
    {
        var deleted = await pipeline.DeleteAsync(new RecordRef(Key, id), version, cancellationToken);
        return deleted.IsSuccess ? deleted : RecordResult<bool>.Fail(deleted.Error!.Code, deleted.Error.Message, deleted.Error.FieldErrors);
    }

    public async Task<RecordResult<ForecastComparison>> CompareAsync(Guid id, Guid? againstId, CancellationToken cancellationToken)
    {
        var current = await HeaderAsync(id, cancellationToken);
        if (current.Error is not null)
        {
            return Forward<ForecastComparison>(current.Error);
        }

        var c = current.Value!;
        var previousId = againstId ?? c.PreviousSubmissionId;
        if (previousId is not { } other)
        {
            return Conflict<ForecastComparison>("Karşılaştırılacak önceki sürüm yok; bir sürüm seçin.");
        }

        var previous = await HeaderAsync(other, cancellationToken);
        if (previous.Error is not null)
        {
            return Forward<ForecastComparison>(previous.Error);
        }

        var p = previous.Value!;
        if (p.PeriodStart != c.PeriodStart || p.PeriodEnd != c.PeriodEnd || p.ScopeType != c.ScopeType || p.ScopeUserId != c.ScopeUserId || p.ReportingCurrency != c.ReportingCurrency)
        {
            return Conflict<ForecastComparison>("Yalnızca aynı dönem, kapsam ve para birimindeki sürümler karşılaştırılır.");
        }

        var visibility = await VisibilityAsync(cancellationToken);
        if (!visibility.ItemsVisible)
        {
            return Conflict<ForecastComparison>("Satırları görmek için fırsat ve tutar izni gerekir.");
        }

        var now = await LoadItemsAsync(c.Id, visibility, cancellationToken);
        var before = await LoadItemsAsync(p.Id, visibility, cancellationToken);
        var nowMap = now.ToDictionary(i => i.OpportunityId);
        var beforeMap = before.ToDictionary(i => i.OpportunityId);

        static ForecastCompareEntry Entry(ForecastItem? b, ForecastItem? a, string? outcome) =>
            new((a ?? b)!.OpportunityId, (a ?? b)!.OpportunityNumber, (a ?? b)!.Title,
                b is null ? null : ForecastRules.FinalAmount(b.BaseAmount, b.AdjustmentAmount), a is null ? null : ForecastRules.FinalAmount(a.BaseAmount, a.AdjustmentAmount),
                b?.Category, a?.Category, outcome);

        var removedIds = beforeMap.Keys.Where(k => !nowMap.ContainsKey(k)).ToList();
        var states = await planning.OpportunityStatesAsync(removedIds, cancellationToken);

        string Outcome(Guid opportunityId) => !states.TryGetValue(opportunityId, out var s) ? "Deleted"
            : s.Status == OpportunityStatus.Open && (s.ExpectedCloseDate is not { } d || d < c.PeriodStart || d > c.PeriodEnd) ? "Postponed"
            : s.Status is OpportunityStatus.Won or OpportunityStatus.Lost or OpportunityStatus.Cancelled or OpportunityStatus.OnHold ? s.Status
            : "Other";

        return RecordResult<ForecastComparison>.Ok(new ForecastComparison(
            c.Id, p.Id, c.VersionNo, p.VersionNo, visibility.AmountsVisible,
            [.. nowMap.Where(kv => !beforeMap.ContainsKey(kv.Key)).Select(kv => Entry(null, kv.Value, null))],
            [.. removedIds.Select(k => Entry(beforeMap[k], null, Outcome(k)))],
            [.. nowMap.Where(kv => beforeMap.TryGetValue(kv.Key, out var b)
                && ForecastRules.FinalAmount(b.BaseAmount, b.AdjustmentAmount) != ForecastRules.FinalAmount(kv.Value.BaseAmount, kv.Value.AdjustmentAmount))
                .Select(kv => Entry(beforeMap[kv.Key], kv.Value, null))],
            [.. nowMap.Where(kv => beforeMap.TryGetValue(kv.Key, out var b) && b.Category != kv.Value.Category).Select(kv => Entry(beforeMap[kv.Key], kv.Value, null))]));
    }

    // ---- Ortak yazma

    private async Task<RecordResult<ForecastDetail>> WriteAsync(
        Guid id, long? version, IReadOnlyList<string> permissions, string kind, Func<ForecastSubmission, SalesPlanningData, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        RecordResult<RecordData> updated;
        using (gate.Open(permissions, effect))
        {
            updated = await pipeline.UpdateAsync(
                new RecordCommand(Key, id, new JsonObject { ["changedAt"] = Iso(time.GetUtcNow()) }, version) { Reason = $"crm.forecast.{kind}" }, cancellationToken);
        }

        return updated.IsSuccess ? await GetAsync(id, cancellationToken) : Forward<ForecastDetail>(updated.Error!);
    }

    private static void EnsureDraft(ForecastSubmission sub)
    {
        if (sub.Status != ForecastStatus.Draft)
        {
            throw new RecordConflictException(sub.Status == ForecastStatus.Locked
                ? "Kilitli tahmin değiştirilemez; gerekçeyle yeniden açıp yeni sürümde düzenleyin."
                : "Sunulan tahmin değiştirilemez; önce taslağa geri çekin.");
        }
    }

    private static void Transition(ForecastSubmission sub, string to)
    {
        if (!ForecastRules.CanTransition(sub.Status, to))
        {
            throw new RecordConflictException($"Tahmin '{sub.Status}' durumundan '{to}' durumuna geçirilemez.");
        }

        sub.Status = to;
    }

    private static RecordValidationException Fail(string field, string message) => new([new RecordFieldError(field, message)]);

    // ---- Anlık görüntü

    private static ForecastItem Snapshot(ForecastItem item, OpportunityRow row, RateBook rates, string currency, DateOnly today, ForecastPolicy policy)
    {
        item.OpportunityId = row.Id;
        item.OpportunityNumber = Trim(row.Number, ForecastItem.NumberMaxLength);
        item.Title = Trim(row.Title, ForecastItem.TitleMaxLength);
        item.OwnerId = row.OwnerId;
        item.Stage = row.Stage;
        item.SourceAmount = row.Amount;
        item.SourceCurrency = row.Currency;
        item.Probability = row.Probability;
        item.ProbabilityManual = row.ProbabilityManual;
        item.ExpectedCloseDate = row.ExpectedCloseDate;
        item.RateUsed = null;
        item.RateDate = null;
        item.RateMissing = false;
        item.BaseAmount = null;
        if (row.Amount is { } amount)
        {
            var rate = rates.Find(row.Currency, currency, today);
            if (rate is null)
            {
                item.RateMissing = true;
            }
            else
            {
                item.RateUsed = rate.Rate;
                item.RateDate = rate.ValidOn;
                item.BaseAmount = Math.Round(amount * rate.Rate, 2, MidpointRounding.AwayFromZero);
            }
        }

        item.BaseCategory = ForecastRules.DefaultCategory(row.Probability, policy);
        return item;
    }

    /// <summary>Yeni satır: kategori varsayılan (temel) kategoriyle başlar.</summary>
    private static ForecastItem NewItem(Guid submissionId, OpportunityRow row, RateBook rates, string currency, DateOnly today, ForecastPolicy policy)
    {
        var item = Snapshot(new ForecastItem { SubmissionId = submissionId }, row, rates, currency, today, policy);
        item.Category = item.BaseCategory;
        return item;
    }

    private static ForecastItem Copy(ForecastItem s, Guid submissionId) => new()
    {
        SubmissionId = submissionId, OpportunityId = s.OpportunityId, OpportunityNumber = s.OpportunityNumber, Title = s.Title, OwnerId = s.OwnerId, Stage = s.Stage,
        SourceAmount = s.SourceAmount, SourceCurrency = s.SourceCurrency, Probability = s.Probability, ProbabilityManual = s.ProbabilityManual,
        ExpectedCloseDate = s.ExpectedCloseDate, RateUsed = s.RateUsed, RateDate = s.RateDate, RateMissing = s.RateMissing, BaseAmount = s.BaseAmount,
        BaseCategory = s.BaseCategory, Category = s.Category, AdjustmentAmount = s.AdjustmentAmount, ProbabilityOverride = s.ProbabilityOverride,
    };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    // ---- Okuma yardımcıları

    private sealed record Visibility(bool ItemsVisible, bool AmountsVisible, IReadOnlySet<Guid>? Owners, bool CanWrite, bool CanSubmit, bool CanLock, bool CanReopen);

    private async Task<Visibility> VisibilityAsync(CancellationToken cancellationToken)
    {
        var canRead = await access.HasPermissionAsync(CrmModule.Permissions.OpportunitiesRead, cancellationToken);
        var amounts = await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken);
        return new Visibility(
            canRead && amounts, amounts, await recordAccess.GetOwnersAsync(CrmModule.Entities.Opportunity, cancellationToken),
            await access.HasPermissionAsync(CrmModule.Permissions.ForecastsWrite, cancellationToken),
            await access.HasPermissionAsync(CrmModule.Permissions.ForecastsSubmit, cancellationToken),
            await access.HasPermissionAsync(CrmModule.Permissions.ForecastsLock, cancellationToken),
            await access.HasPermissionAsync(CrmModule.Permissions.ForecastsReopen, cancellationToken));
    }

    /// <summary>Bugünkü erişim yetkisindeki satırlar: fırsatın <b>bugünkü</b> sorumlusu veri kapsamındaysa (kapsam Tümü ise hepsi) görünür.</summary>
    private async Task<List<ForecastItem>> LoadItemsAsync(Guid submissionId, Visibility visibility, CancellationToken cancellationToken)
    {
        if (!visibility.ItemsVisible)
        {
            return [];
        }

        var items = await planning.ItemsAsync(submissionId, cancellationToken);
        if (visibility.Owners is not { } owners)
        {
            return items;
        }

        var current = await planning.CurrentOwnersAsync([.. items.Select(i => i.OpportunityId)], cancellationToken);
        return [.. items.Where(i => current.TryGetValue(i.OpportunityId, out var owner) && owner is { } o && owners.Contains(o))];
    }

    private async Task<bool> IsVisibleAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var visibility = await VisibilityAsync(cancellationToken);
        if (!visibility.ItemsVisible)
        {
            return false;
        }

        if (visibility.Owners is not { } owners)
        {
            return true;
        }

        var current = await planning.CurrentOwnersAsync([opportunityId], cancellationToken);
        return current.TryGetValue(opportunityId, out var owner) && owner is { } o && owners.Contains(o);
    }

    private async Task<ForecastDetail> DetailAsync(ForecastHeader h, CancellationToken cancellationToken)
    {
        var visibility = await VisibilityAsync(cancellationToken);
        var policy = await PolicyAsync(cancellationToken);
        var today = ReportPeriods.Today(time.GetUtcNow(), policy.TimeZone);
        var warnings = new List<string>();
        var items = await LoadItemsAsync(h.Id, visibility, cancellationToken);
        var allCount = visibility.ItemsVisible ? await planning.ItemCountAsync(h.Id, cancellationToken) : 0;
        if (!visibility.ItemsVisible)
        {
            warnings.Add("Satırları görmek için fırsat ve fırsat tutarı izinleri gerekir.");
        }
        else if (items.Count < allCount)
        {
            warnings.Add("Bu tahmindeki bazı satırlar veri kapsamınızın dışında olduğundan gösterilmez; toplamlar yalnızca gösterilen satırlardan hesaplanır.");
        }

        var owners = await planning.CurrentOwnersAsync([.. items.Select(i => i.OpportunityId)], cancellationToken);
        var views = items.OrderBy(i => i.OpportunityNumber, StringComparer.Ordinal).Select(i => View(i, owners)).ToList();
        var adjustments = visibility.ItemsVisible
            ? (await planning.AdjustmentsAsync(h.Id, cancellationToken))
                .Where(a => items.Any(i => i.OpportunityId == a.OpportunityId))
                .Select(a => new ForecastAdjustmentView(a.Id, a.ItemId, a.OpportunityId, a.Kind, a.FromValue, a.ToValue, a.Reason, a.ChangedBy, a.CreatedAt)).ToList()
            : [];

        // Gerçekleşen: kilitli sürümde kilit anındaki değer; aksi halde canlı. Veri kapsamı kısıtlı kullanıcı tenant tahmininde gerçekleşeni göremez (kapsam dışı satış eksik kalırdı).
        var period = new ReportPeriod(Enum.Parse<PeriodKind>(h.PeriodKind), h.PeriodStart, h.PeriodEnd.AddDays(1), string.Empty);
        decimal? realized = null;
        var live = false;
        var scopeBlocksRealized = visibility.Owners is not null && h.ScopeType == TargetScope.Tenant;
        if (visibility.ItemsVisible && !scopeBlocksRealized)
        {
            if (h.Status == ForecastStatus.Locked && h.RealizedAtLock is { } locked)
            {
                realized = locked;
                if (h.RealizedPartial)
                {
                    warnings.Add("Kilit anındaki gerçekleşen satış kısmiydi (kur ya da tutar eksik).");
                }
            }
            else
            {
                var total = await RealizedAsync(period, h.ScopeUserId, h.ReportingCurrency, today, cancellationToken);
                if (total.Error is null)
                {
                    realized = total.Total!.Amount;
                    live = true;
                    if (total.Total.IsPartial)
                    {
                        warnings.Add("Gerçekleşen satış kısmi: bazı kazanılmış fırsatların kuru ya da tutarı eksik.");
                    }
                }
            }
        }
        else if (scopeBlocksRealized)
        {
            warnings.Add("Veri kapsamınız kısıtlı olduğundan tenant tahmininin gerçekleşen satışı gösterilmez.");
        }

        var lines = views.Select(v => new ForecastLine(v.Category, v.FinalAmount, v.EffectiveProbability)).ToList();
        var scenarios = visibility.ItemsVisible ? ForecastRules.Scenarios(realized ?? 0m, lines) : null;
        var byCategory = ForecastCategory.All.Select(c =>
        {
            var inCategory = views.Where(v => v.Category == c).ToList();
            return new ForecastCategoryTotal(
                c, inCategory.Count, inCategory.Sum(v => v.FinalAmount ?? 0m),
                c == ForecastCategory.Omitted ? 0m : inCategory.Sum(v => v.FinalAmount is { } a ? ForecastRules.Weighted(a, v.EffectiveProbability) : 0m));
        }).ToList();

        var missingRate = views.Count(v => v.RateMissing);
        var missingProbability = views.Count(v => v.EffectiveProbability is null && v.Category != ForecastCategory.Omitted);
        if (missingRate > 0)
        {
            warnings.Add($"{missingRate} satırın kuru yoktu; bu satırlar hiçbir toplama girmiyor. Kur tanımlayıp taslağı yenileyin.");
        }

        if (missingProbability > 0)
        {
            warnings.Add($"{missingProbability} satırın olasılığı yok; ağırlıklı toplama girmiyor (sıfır sayılmadı).");
        }

        var noDate = views.Count(v => v.ExpectedCloseDate is null);
        if (noDate > 0)
        {
            warnings.Add($"{noDate} satırın beklenen kapanış tarihi yok.");
        }

        var accuracy = AccuracyOf(h, scenarios, realized, live, period, today, policy.TimeZone, visibility.ItemsVisible && !scopeBlocksRealized);
        var open = h.Status == ForecastStatus.Locked
            && (await planning.VersionsAsync(h.PeriodStart, h.PeriodEnd, h.ScopeType, h.ScopeUserId, h.ReportingCurrency, cancellationToken)).Any(v => v.Status != ForecastStatus.Locked);
        var capabilities = new ForecastCapabilities(
            visibility.CanWrite && h.Status == ForecastStatus.Draft,
            visibility.CanSubmit && h.Status == ForecastStatus.Draft,
            visibility.CanSubmit && h.Status == ForecastStatus.Submitted,
            visibility.CanLock && h.Status == ForecastStatus.Submitted,
            visibility.CanReopen && h.Status == ForecastStatus.Locked && !open,
            visibility.CanWrite && h.Status == ForecastStatus.Draft);

        return new ForecastDetail(
            h, visibility.ItemsVisible, visibility.AmountsVisible, views, adjustments, byCategory, scenarios, realized, live, accuracy, capabilities, warnings, Definitions);
    }

    private static ForecastAccuracy AccuracyOf(
        ForecastHeader h, ForecastScenarios? scenarios, decimal? actual, bool live, ReportPeriod period, DateOnly today, TimeZoneInfo zone, bool visible)
    {
        if (h.Status != ForecastStatus.Locked || h.LockedAt is not { } lockedAt || scenarios is null || !visible)
        {
            return new ForecastAccuracy("NotLocked", null, null, null, null);
        }

        var horizon = period.LastDay.DayNumber - ReportPeriods.Today(lockedAt, zone).DayNumber;
        if (!period.HasEnded(today))
        {
            return new ForecastAccuracy("Pending", horizon, null, null, null);
        }

        // Dönem bittiyse canlı gerçekleşen bilinen son değerdir (kilit anındaki değil): kilitli tahmin ile sonuç karşılaştırılır.
        return new ForecastAccuracy(
            "Ready", horizon, SalesMetrics.Variance(scenarios.CommitScenario, actual ?? 0m), SalesMetrics.Variance(scenarios.WeightedScenario, actual ?? 0m), actual);
    }

    private static ForecastItemView View(ForecastItem i, Dictionary<Guid, Guid?> owners)
    {
        var final = ForecastRules.FinalAmount(i.BaseAmount, i.AdjustmentAmount);
        var effective = ForecastRules.EffectiveProbability(i.Probability, i.ProbabilityOverride);
        var flags = new List<string>();
        if (i.SourceAmount is null)
        {
            flags.Add("MissingAmount");
        }

        if (i.RateMissing)
        {
            flags.Add("MissingRate");
        }

        if (effective is null)
        {
            flags.Add("MissingProbability");
        }

        if (i.ExpectedCloseDate is null)
        {
            flags.Add("MissingCloseDate");
        }

        return new ForecastItemView(
            i.Id, i.OpportunityId, i.OpportunityNumber, i.Title, i.OwnerId, owners.TryGetValue(i.OpportunityId, out var current) ? current : null, i.Stage, i.SourceAmount,
            i.SourceCurrency, i.Probability, i.ProbabilityOverride is not null ? "Override" : i.Probability is null ? "Unknown" : i.ProbabilityManual ? "Manual" : "Stage",
            i.ExpectedCloseDate, i.RateUsed, i.RateDate, i.RateMissing, i.BaseAmount, i.BaseCategory, i.Category, i.AdjustmentAmount, final, i.ProbabilityOverride, effective,
            final is { } f && i.Category != ForecastCategory.Omitted ? ForecastRules.Weighted(f, effective) : null, flags);
    }

    private static ForecastHeader ToHeader(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var guid) ? guid : null;
        DateOnly Date(string name) => DateOnly.TryParseExact(Text(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : default;
        DateTimeOffset? Time(string name) => DateTimeOffset.TryParse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        decimal? Money(string name) => v[name] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : null;

        return new ForecastHeader(
            record.Id, record.Version, Text("periodKind") ?? nameof(PeriodKind.Month), Date("periodStart"), Date("periodEnd"), Text("scopeType") ?? TargetScope.Tenant,
            Id("scopeUserId"), Text("reportingCurrency") ?? Opportunity.DefaultCurrency, v["versionNo"] is JsonValue n && n.TryGetValue<int>(out var no) ? no : 1,
            Text("status") ?? ForecastStatus.Draft, Id("previousSubmissionId"), Text("note"), Time("submittedAt"), Id("submittedBy"), Time("lockedAt"), Id("lockedBy"),
            Text("reopenReason"), Text("returnReason"), Money("realizedAtLock"), v["realizedPartial"] is JsonValue p && p.TryGetValue<bool>(out var partial) && partial,
            Time("refreshedAt"), Time("changedAt"));
    }

    private async Task<RecordResult<ForecastHeader>> HeaderAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        return record.IsSuccess
            ? RecordResult<ForecastHeader>.Ok(ToHeader(record.Value!))
            : RecordResult<ForecastHeader>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
    }

    // ---- Kapsam ve veri

    /// <summary>
    /// Anlık görüntü eksiksiz olsun diye: tenant tahmini yalnızca veri kapsamı Tümü olan kullanıcıyla; kullanıcı tahmini kapsamında o kullanıcı olanla hazırlanır/kilitlenir.
    /// Tutar ve olasılık hassastır; izni olmayan kullanıcı tahmin hazırlayamaz.
    /// </summary>
    private async Task<RecordResult<ForecastDetail>?> GuardAsync(string scopeType, Guid? scopeUserId, bool requireAmounts, CancellationToken cancellationToken)
    {
        if (requireAmounts && !await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken))
        {
            return RecordResult<ForecastDetail>.Fail(RecordErrorCode.PermissionDenied, $"Tahmin için '{CrmModule.Permissions.OpportunityAmountsRead}' izni gerekir.");
        }

        if (!await access.HasPermissionAsync(CrmModule.Permissions.OpportunitiesRead, cancellationToken))
        {
            return RecordResult<ForecastDetail>.Fail(RecordErrorCode.PermissionDenied, $"Tahmin için '{CrmModule.Permissions.OpportunitiesRead}' izni gerekir.");
        }

        var owners = await recordAccess.GetOwnersAsync(CrmModule.Entities.Opportunity, cancellationToken);
        if (owners is null)
        {
            return null;
        }

        if (scopeType == TargetScope.User && scopeUserId is { } user && owners.Contains(user))
        {
            return null;
        }

        return RecordResult<ForecastDetail>.Fail(
            RecordErrorCode.PermissionDenied, "Bu tahminin kapsamı veri kapsamınızı aşıyor; anlık görüntü eksik kalacağından işlem yapılamaz.");
    }

    private async Task<OpportunityPage> ReadOpenAsync(ReportPeriod period, Guid? ownerId, CancellationToken cancellationToken) =>
        await reader.ReadAsync(SalesOpportunityReader.Filters(
            ownerId, "status", OpportunityStatus.Open, "closeFrom", SalesOpportunityReader.Iso(period.Start), "closeTo", SalesOpportunityReader.Iso(period.LastDay)), cancellationToken);

    private sealed record RealizedResult(MoneyTotal? Total, bool Truncated, RecordError? Error);

    private async Task<RealizedResult> RealizedAsync(ReportPeriod period, Guid? ownerId, string currency, DateOnly today, CancellationToken cancellationToken)
    {
        var closed = await reader.ReadAsync(SalesOpportunityReader.Filters(
            ownerId, "status", OpportunityStatus.Won, "actualCloseFrom", SalesOpportunityReader.Iso(period.Start), "actualCloseTo", SalesOpportunityReader.Iso(period.LastDay)), cancellationToken);
        if (closed.Error is not null)
        {
            return new RealizedResult(null, false, closed.Error);
        }

        var rates = await RatesAsync(cancellationToken);
        return new RealizedResult(SalesMetrics.Total(closed.Rows.Select(r => r.ToFact()).ToList(), SalesCard.Realized, period, currency, rates, today), closed.Truncated, null);
    }

    private async Task<RateBook> RatesAsync(CancellationToken cancellationToken) =>
        new(await planning.RatesAsync(cancellationToken));

    private async Task<SalesReportingPolicy> PolicyAsync(CancellationToken cancellationToken) =>
        policyProvider is null ? SalesReportingPolicy.Default : await policyProvider.GetAsync(cancellationToken);

    private static string Iso(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static string? Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && decimal.Round(value, 2) == value ? value : null;

    private static RecordResult<T> Forward<T>(RecordError error) => RecordResult<T>.Fail(error.Code, error.Message, error.FieldErrors);

    private static RecordResult<T> Invalid<T>(string field, string message) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    private static RecordResult<ForecastDetail> Invalid(string field, string message) => Invalid<ForecastDetail>(field, message);

    private static RecordResult<T> Conflict<T>(string message) => RecordResult<T>.Fail(RecordErrorCode.Conflict, message);

    private static RecordResult<ForecastDetail> Conflict(string message) => Conflict<ForecastDetail>(message);
}
