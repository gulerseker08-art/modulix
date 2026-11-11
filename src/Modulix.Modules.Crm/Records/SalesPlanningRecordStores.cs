using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Satış tahmini iş akışı kapısı (istek ömürlü). Sunum, kilit ve satır alanları genel kayıt uçlarından yazılamaz; yalnızca <c>ForecastWorkflow</c> bu kapıyı
/// açarak boru hattı üzerinden yazar. Kapı açıkken tahmin deposu, eylemin izinlerinden birini ister ve <see cref="Effect"/>'i aynı işlemde (aynı kayıt,
/// audit ve olay adımı) çalıştırır: satırlar ve düzeltmeler tahminle birlikte, tek işlemde yazılır.
/// </summary>
internal sealed class ForecastWorkflowGate
{
    public IReadOnlyList<string>? Permissions { get; private set; }

    public bool IsOpen => Permissions is not null;

    /// <summary>Tahmin kaydı uygulandıktan sonra aynı işlemde çalışır (satır ekleme, düzeltme kaydı).</summary>
    public Func<ForecastSubmission, SalesPlanningData, CancellationToken, Task>? Effect { get; private set; }

    public IDisposable Open(IReadOnlyList<string> permissions, Func<ForecastSubmission, SalesPlanningData, CancellationToken, Task>? effect = null)
    {
        Permissions = permissions;
        Effect = effect;
        return new Closer(this);
    }

    private sealed class Closer(ForecastWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permissions = null;
            gate.Effect = null;
        }
    }
}

/// <summary>
/// Satış hedefi (ADR-0035). Metrik, kapsam, para birimi ve dönem oluştururken verilir, sonra değişmez; yalnızca hedef değeri (gerekçeyle, sürümlü) ve not
/// değişir. Aynı kapsam, metrik ve para biriminde dönemler çakışamaz. Tutar hedefi para birimi ister, adet hedefi tam sayıdır ve para birimi taşımaz.
/// </summary>
internal sealed class SalesTargetRecordStore(CrmStoreContext context) : CrmRecordStore<SalesTarget>(context)
{
    public override string EntityKey => CrmModule.Entities.SalesTarget;

    protected override string ReadPermission => CrmModule.Permissions.SalesTargetsRead;

    protected override string WritePermission => CrmModule.Permissions.SalesTargetsManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<SalesTarget>> Fields { get; } =
    [
        Field.Choice<SalesTarget>("metric", TargetMetric.All, t => t.Metric, (t, v) => t.Metric = v),
        Field.Choice<SalesTarget>("scopeType", TargetScope.All, t => t.ScopeType, (t, v) => t.ScopeType = v),
        Field.Owner<SalesTarget>("scopeUserId", t => t.ScopeUserId, (t, v) => t.ScopeUserId = v),
        Field.OptionalCurrency<SalesTarget>("currency", t => t.Currency, (t, v) => t.Currency = v),
        Field.Date<SalesTarget>("periodStart", t => t.PeriodStart == default ? null : t.PeriodStart, (t, v) => { if (v is { } day) { t.PeriodStart = day; } }),
        Field.Date<SalesTarget>("periodEnd", t => t.PeriodEnd == default ? null : t.PeriodEnd, (t, v) => { if (v is { } day) { t.PeriodEnd = day; } }),
        Field.Money<SalesTarget>("value", t => t.Value == 0 ? null : t.Value, (t, v) => { if (v is { } value) { t.Value = value; } }),
        Field.Count<SalesTarget>("revisionNo", t => t.RevisionNo, (t, v) => t.RevisionNo = v).AsSystem(),
        Field.Text<SalesTarget>("note", SalesTarget.NoteMaxLength, t => t.Note, (t, v) => t.Note = v),
        Field.Text<SalesTarget>("changeReason", ForecastRules.ReasonMaxLength, _ => null, (t, v) => t.PendingReason = v).AsWriteOnly(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SalesTarget>> Filters { get; } =
        new Dictionary<string, RecordFilter<SalesTarget>>
        {
            ["metric"] = (q, v, _) => TargetMetric.All.Contains(v) ? q.Where(t => t.Metric == v) : null,
            ["scopeType"] = (q, v, _) => TargetScope.All.Contains(v) ? q.Where(t => t.ScopeType == v) : null,
            ["scopeUserId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(t => t.ScopeUserId == id) : null,
            // Dönemle kesişen hedefler: [from, to] aralığıyla çakışır.
            ["from"] = (q, v, _) => FilterValue.Date(v) is { } day ? q.Where(t => t.PeriodEnd >= day) : null,
            ["to"] = (q, v, _) => FilterValue.Date(v) is { } day ? q.Where(t => t.PeriodStart <= day) : null,
        };

    protected override IOrderedQueryable<SalesTarget> Order(IQueryable<SalesTarget> source) =>
        source.OrderByDescending(t => t.PeriodStart).ThenBy(t => t.Metric).ThenBy(t => t.ScopeType);

    protected override IQueryable<SalesTarget> Search(IQueryable<SalesTarget> source, string term) =>
        source.Where(t => t.Note != null && t.Note.Contains(term));

    protected override async Task AppliedAsync(SalesTarget entity, JsonObject values, CancellationToken cancellationToken)
    {
        var original = await Context.Data.Set<SalesTarget>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == entity.Id, cancellationToken);
        var errors = new List<RecordFieldError>();

        if (original is null)
        {
            ValidateIdentity(entity, errors);
        }
        else
        {
            RejectIdentityChange(entity, original, errors);
        }

        if (entity.Value <= 0)
        {
            errors.Add(new RecordFieldError("value", "Hedef sıfırdan büyük olmalıdır."));
        }
        else if (entity.Metric == TargetMetric.WonCount && entity.Value != decimal.Truncate(entity.Value))
        {
            errors.Add(new RecordFieldError("value", "Adet hedefi tam sayı olmalıdır."));
        }

        if (errors.Count == 0 && original is null)
        {
            var overlapping = await Context.Data.Set<SalesTarget>().AsNoTracking()
                .Where(o => o.Metric == entity.Metric && o.ScopeType == entity.ScopeType && o.ScopeUserId == entity.ScopeUserId && o.Currency == entity.Currency)
                .Select(o => new { o.PeriodStart, o.PeriodEnd }).ToListAsync(cancellationToken);
            if (overlapping.Any(o => TargetRules.Overlaps(o.PeriodStart, o.PeriodEnd.AddDays(1), entity.PeriodStart, entity.PeriodEnd.AddDays(1))))
            {
                errors.Add(new RecordFieldError("periodStart", "Bu kapsam, metrik ve para birimi için dönem mevcut bir hedefle çakışıyor."));
            }
        }

        if (errors.Count == 0 && original is not null && original.Value != entity.Value)
        {
            if (ForecastRules.ValidateReason(entity.PendingReason) is { } reasonError)
            {
                errors.Add(new RecordFieldError("changeReason", reasonError));
            }
            else
            {
                // Dönem içi hedef değişimi sürümlenir: eski ve yeni değer ve gerekçe ayrı kayıtta kalır.
                entity.RevisionNo = original.RevisionNo + 1;
                Context.Data.Set<SalesTargetRevision>().Add(new SalesTargetRevision
                {
                    TargetId = entity.Id,
                    RevisionNo = entity.RevisionNo,
                    OldValue = original.Value,
                    NewValue = entity.Value,
                    Reason = entity.PendingReason!.Trim(),
                    ChangedBy = Context.CurrentUserId,
                });
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private static void ValidateIdentity(SalesTarget entity, List<RecordFieldError> errors)
    {
        if (entity.PeriodStart == default || entity.PeriodEnd == default)
        {
            errors.Add(new RecordFieldError("periodStart", "Dönem başı ve sonu zorunludur."));
        }
        else if (entity.PeriodEnd < entity.PeriodStart)
        {
            errors.Add(new RecordFieldError("periodEnd", "Dönem sonu başından önce olamaz."));
        }
        else if (entity.PeriodEnd.DayNumber - entity.PeriodStart.DayNumber + 1 > SalesTarget.MaxSpanDays)
        {
            errors.Add(new RecordFieldError("periodEnd", $"Hedef dönemi en fazla {SalesTarget.MaxSpanDays} gün olabilir."));
        }

        if (entity.ScopeType == TargetScope.User && entity.ScopeUserId is null)
        {
            errors.Add(new RecordFieldError("scopeUserId", "Kullanıcı hedefinde kullanıcı zorunludur."));
        }
        else if (entity.ScopeType == TargetScope.Tenant && entity.ScopeUserId is not null)
        {
            errors.Add(new RecordFieldError("scopeUserId", "Tenant hedefinde kullanıcı verilmez."));
        }

        if (entity.Metric == TargetMetric.WonAmount && string.IsNullOrEmpty(entity.Currency))
        {
            errors.Add(new RecordFieldError("currency", "Tutar hedefinde para birimi zorunludur."));
        }
        else if (entity.Metric == TargetMetric.WonCount && entity.Currency is not null)
        {
            errors.Add(new RecordFieldError("currency", "Adet hedefinde para birimi olmaz."));
        }
    }

    private static void RejectIdentityChange(SalesTarget entity, SalesTarget original, List<RecordFieldError> errors)
    {
        const string message = "Hedef oluşturulduktan sonra değiştirilemez; yeni dönem için yeni hedef açın.";
        if (entity.Metric != original.Metric)
        {
            errors.Add(new RecordFieldError("metric", message));
        }

        if (entity.ScopeType != original.ScopeType || entity.ScopeUserId != original.ScopeUserId)
        {
            errors.Add(new RecordFieldError("scopeType", message));
        }

        if (entity.Currency != original.Currency)
        {
            errors.Add(new RecordFieldError("currency", message));
        }

        if (entity.PeriodStart != original.PeriodStart || entity.PeriodEnd != original.PeriodEnd)
        {
            errors.Add(new RecordFieldError("periodStart", message));
        }
    }

    protected override async Task BeforeDeleteAsync(SalesTarget entity, CancellationToken cancellationToken)
    {
        var revisions = await Context.Data.Set<SalesTargetRevision>().Where(r => r.TargetId == entity.Id).ToListAsync(cancellationToken);
        Context.Data.Set<SalesTargetRevision>().RemoveRange(revisions);
    }
}

/// <summary>
/// Satış tahmini başlığı (ADR-0035). Dönem, kapsam, durum, sürüm ve damgalar yalnızca <see cref="ForecastWorkflowGate"/> açıkken yazılır; genel uçlardan yalnızca
/// not (Taslak durumunda) değişir. Satırlar ve düzeltmeler kapı etkisiyle aynı işlemde yazılır. Kilitli sürüm değişmez ve silinmez.
/// </summary>
internal sealed class ForecastSubmissionRecordStore(CrmStoreContext context, ForecastWorkflowGate gate) : CrmRecordStore<ForecastSubmission>(context), IRecordStore
{
    public override string EntityKey => CrmModule.Entities.ForecastSubmission;

    protected override string ReadPermission => CrmModule.Permissions.ForecastsRead;

    protected override string WritePermission => CrmModule.Permissions.ForecastsWrite;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool GateOpen => gate.IsOpen;

    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) =>
        operation is RecordOperation.Create or RecordOperation.Update && gate.Permissions is { } permissions ? permissions : null;

    protected override IReadOnlyList<RecordField<ForecastSubmission>> Fields { get; } =
    [
        Field.Choice<ForecastSubmission>("periodKind", Enum.GetNames<PeriodKind>(), s => s.PeriodKind, (s, v) => s.PeriodKind = v).AsSystem(),
        Field.Date<ForecastSubmission>("periodStart", s => s.PeriodStart == default ? null : s.PeriodStart, (s, v) => { if (v is { } d) { s.PeriodStart = d; } }).AsSystem(),
        Field.Date<ForecastSubmission>("periodEnd", s => s.PeriodEnd == default ? null : s.PeriodEnd, (s, v) => { if (v is { } d) { s.PeriodEnd = d; } }).AsSystem(),
        Field.Choice<ForecastSubmission>("scopeType", TargetScope.All, s => s.ScopeType, (s, v) => s.ScopeType = v).AsSystem(),
        Field.Id<ForecastSubmission>("scopeUserId", s => s.ScopeUserId, (s, v) => s.ScopeUserId = v).AsSystem(),
        Field.Currency<ForecastSubmission>("reportingCurrency", s => s.ReportingCurrency, (s, v) => s.ReportingCurrency = v).AsSystem(),
        Field.Count<ForecastSubmission>("versionNo", s => s.VersionNo, (s, v) => s.VersionNo = v).AsSystem(),
        Field.Choice<ForecastSubmission>("status", ForecastStatus.All, s => s.Status, (s, v) => s.Status = v).AsSystem(),
        Field.Id<ForecastSubmission>("previousSubmissionId", s => s.PreviousSubmissionId, (s, v) => s.PreviousSubmissionId = v).AsSystem(),
        Field.Text<ForecastSubmission>("note", ForecastSubmission.NoteMaxLength, s => s.Note, (s, v) => s.Note = v),
        Field.Time<ForecastSubmission>("submittedAt", s => s.SubmittedAt, (s, v) => s.SubmittedAt = v).AsSystem(),
        Field.Id<ForecastSubmission>("submittedBy", s => s.SubmittedBy, (s, v) => s.SubmittedBy = v).AsSystem(),
        Field.Time<ForecastSubmission>("lockedAt", s => s.LockedAt, (s, v) => s.LockedAt = v).AsSystem(),
        Field.Id<ForecastSubmission>("lockedBy", s => s.LockedBy, (s, v) => s.LockedBy = v).AsSystem(),
        Field.Text<ForecastSubmission>("reopenReason", ForecastSubmission.NoteMaxLength, s => s.ReopenReason, (s, v) => s.ReopenReason = v).AsSystem(),
        Field.Text<ForecastSubmission>("returnReason", ForecastSubmission.NoteMaxLength, s => s.ReturnReason, (s, v) => s.ReturnReason = v).AsSystem(),
        Field.Money<ForecastSubmission>("realizedAtLock", s => s.RealizedAtLock, (s, v) => s.RealizedAtLock = v).AsSystem(),
        Field.Flag<ForecastSubmission>("realizedPartial", s => s.RealizedPartial, (s, v) => s.RealizedPartial = v).AsSystem(),
        Field.Time<ForecastSubmission>("refreshedAt", s => s.RefreshedAt, (s, v) => s.RefreshedAt = v).AsSystem(),
        Field.Time<ForecastSubmission>("changedAt", s => s.ChangedAt, (s, v) => s.ChangedAt = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ForecastSubmission>> Filters { get; } =
        new Dictionary<string, RecordFilter<ForecastSubmission>>
        {
            ["status"] = (q, v, _) => ForecastStatus.All.Contains(v) ? q.Where(s => s.Status == v) : null,
            ["scopeUserId"] = (q, v, c) => FilterValue.User(v, c) is { } id ? q.Where(s => s.ScopeUserId == id) : null,
            ["periodStart"] = (q, v, _) => FilterValue.Date(v) is { } day ? q.Where(s => s.PeriodStart == day) : null,
            ["from"] = (q, v, _) => FilterValue.Date(v) is { } day ? q.Where(s => s.PeriodEnd >= day) : null,
            ["to"] = (q, v, _) => FilterValue.Date(v) is { } day ? q.Where(s => s.PeriodStart <= day) : null,
        };

    protected override IOrderedQueryable<ForecastSubmission> Order(IQueryable<ForecastSubmission> source) =>
        source.OrderByDescending(s => s.PeriodStart).ThenBy(s => s.ScopeType).ThenByDescending(s => s.VersionNo);

    protected override IQueryable<ForecastSubmission> Search(IQueryable<ForecastSubmission> source, string term) =>
        source.Where(s => s.Note != null && s.Note.Contains(term));

    /// <summary>Tahmin genel kayıt uçlarından oluşturulamaz: anlık görüntü, sürüm ve dönem yalnızca iş akışıyla (kapı açıkken) kurulur.</summary>
    protected override Task<(JsonObject Values, bool Linked)> PrepareNewAsync(ForecastSubmission entity, JsonObject values, CancellationToken cancellationToken) =>
        gate.IsOpen
            ? Task.FromResult((values, false))
            : throw new RecordValidationException([new RecordFieldError("periodStart", "Tahmin yalnızca tahmin ekranından (iş akışıyla) oluşturulur.")]);

    protected override async Task AppliedAsync(ForecastSubmission entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!gate.IsOpen)
        {
            // Genel uçtan yalnızca not değişir ve yalnızca Taslak durumunda; kilitli/sunulan tahmin değişmez.
            if (values.ContainsKey("note") && entity.Status != ForecastStatus.Draft)
            {
                throw new RecordValidationException([new RecordFieldError("note", "Not yalnızca taslak tahminde değiştirilir.")]);
            }

            return;
        }

        if (gate.Effect is { } effect)
        {
            await effect(entity, new SalesPlanningData(Context.Data), cancellationToken);
        }
    }

    protected override async Task BeforeDeleteAsync(ForecastSubmission entity, CancellationToken cancellationToken)
    {
        if (entity.Status != ForecastStatus.Draft)
        {
            throw new RecordConflictException("Yalnızca taslak tahmin silinebilir; sunulan ve kilitli tahmin korunur.");
        }

        var items = await Context.Data.Set<ForecastItem>().Where(i => i.SubmissionId == entity.Id).ToListAsync(cancellationToken);
        var adjustments = await Context.Data.Set<ForecastAdjustment>().Where(a => a.SubmissionId == entity.Id).ToListAsync(cancellationToken);
        Context.Data.Set<ForecastAdjustment>().RemoveRange(adjustments);
        Context.Data.Set<ForecastItem>().RemoveRange(items);
    }
}
