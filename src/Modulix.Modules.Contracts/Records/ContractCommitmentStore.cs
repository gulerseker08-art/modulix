using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Taahhüt kayıt deposu (ADR-0022, dilim 7). Görünürlük ve veri kapsamı üst sözleşmeyi izler. Tanım alanları <c>contracts.commitments.write</c> ile yazılır;
/// durum ve gerçekleşme değişiklikleri sistem alanı/iş akışı kapısıdır (<see cref="IContractCommitments"/>). Oluşturma ve takvim değişikliği gerçekleşme satırlarını üretir
/// (tek seferlikte bir, dönemselde tekrar kuralından); gerçekleşme tamamlanmış taahhüdün takvimi ve ölçütü değişmez (önceki kayıtlar yeniden yazılmaz).
/// </summary>
internal sealed class ContractCommitmentRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractCommitment>(context)
{
    /// <summary>Takvim ve ölçüm alanları: tamamlanmış/ihlal/muaf gerçekleşmesi olan taahhütte değiştirilemez (aynı değerle yeniden gönderilmesi değişiklik sayılmaz).</summary>
    private static readonly string[] LockedAfterHistory =
    [
        "kind", "startDate", "dueDate", "recurrence", "recurrenceEndDate", "metricName", "unit", "direction", "targetValue", "tolerance",
    ];

    private Dictionary<string, string?>? scheduleBefore;

    private static Dictionary<string, string?> ScheduleValues(ContractCommitment c) => new()
    {
        ["kind"] = c.Kind, ["startDate"] = c.StartDate.ToString("O"), ["dueDate"] = c.DueDate.ToString("O"), ["recurrence"] = c.Recurrence,
        ["recurrenceEndDate"] = c.RecurrenceEndDate?.ToString("O"), ["metricName"] = c.MetricName, ["unit"] = c.Unit, ["direction"] = c.Direction,
        ["targetValue"] = c.TargetValue?.ToString(System.Globalization.CultureInfo.InvariantCulture), ["tolerance"] = c.Tolerance?.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    protected override void OnUpdating(ContractCommitment entity) => scheduleBefore = ScheduleValues(entity);

    public override string EntityKey => ContractsModule.Entities.ContractCommitment;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.CommitmentsWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => ContractsModule.Entities.Contract;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ContractCommitment>> Fields { get; } =
    [
        Field.Reference<ContractCommitment, Contract>("contractId", "Sözleşme bulunamadı.", c => c.ContractId, (c, v) => c.ContractId = v!.Value, required: true, createOnly: true),
        Field.Text<ContractCommitment>("title", ContractCommitment.TitleMaxLength, c => c.Title, (c, v) => c.Title = v!, required: true),
        Field.Text<ContractCommitment>("description", ContractCommitment.TextMaxLength, c => c.Description, (c, v) => c.Description = v),
        Field.ListChoice<ContractCommitment>("category", ContractsModule.Lists.CommitmentCategory, c => c.Category, (c, v) => c.Category = v ?? string.Empty),
        Field.Choice<ContractCommitment>("obligor", CommitmentObligor.All, c => c.Obligor, (c, v) => c.Obligor = v),
        Field.Owner<ContractCommitment>("responsibleUserId", c => c.ResponsibleUserId, (c, v) => c.ResponsibleUserId = v),
        Field.Owner<ContractCommitment>("escalationUserId", c => c.EscalationUserId, (c, v) => c.EscalationUserId = v),
        Field.Choice<ContractCommitment>("kind", CommitmentKind.All, c => c.Kind, (c, v) => c.Kind = v),
        Field.Date<ContractCommitment>("startDate", c => c.StartDate == default ? null : c.StartDate, (c, v) => { if (v is { } d) { c.StartDate = d; } }),
        Field.Date<ContractCommitment>("dueDate", c => c.DueDate == default ? null : c.DueDate, (c, v) => { if (v is { } d) { c.DueDate = d; } }),
        Field.OptionalChoice<ContractCommitment>("recurrence", CommitmentRecurrence.All, c => c.Recurrence, (c, v) => c.Recurrence = v),
        Field.Date<ContractCommitment>("recurrenceEndDate", c => c.RecurrenceEndDate, (c, v) => c.RecurrenceEndDate = v),
        Field.Integer<ContractCommitment>("graceDays", c => c.GraceDays, (c, v) => c.GraceDays = v ?? 0, max: 365),
        Field.Integer<ContractCommitment>("escalateAfterDays", c => c.EscalateAfterDays, (c, v) => c.EscalateAfterDays = v ?? 0, max: 365),
        Field.Text<ContractCommitment>("metricName", ContractCommitment.MetricMaxLength, c => c.MetricName, (c, v) => c.MetricName = v),
        Field.Text<ContractCommitment>("unit", ContractCommitment.UnitMaxLength, c => c.Unit, (c, v) => c.Unit = v),
        Field.OptionalChoice<ContractCommitment>("direction", CommitmentDirection.All, c => c.Direction, (c, v) => c.Direction = v),
        Field.Quantity<ContractCommitment>("targetValue", c => c.TargetValue, (c, v) => c.TargetValue = v),
        Field.Quantity<ContractCommitment>("tolerance", c => c.Tolerance, (c, v) => c.Tolerance = v),
        Field.Reference<ContractCommitment, ContractCommitment>("dependsOnCommitmentId", "Bağımlı olunan taahhüt bulunamadı.", c => c.DependsOnCommitmentId, (c, v) => c.DependsOnCommitmentId = v),
        Field.Text<ContractCommitment>("consequenceNote", ContractCommitment.TextMaxLength, c => c.ConsequenceNote, (c, v) => c.ConsequenceNote = v),
        Field.Text<ContractCommitment>("status", Contract.StatusMaxLength, c => c.Status, (c, v) => c.Status = v!).AsSystem(),
        Field.Id<ContractCommitment>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractCommitment>> Filters { get; } = new Dictionary<string, RecordFilter<ContractCommitment>>
    {
        ["contractId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.ContractId == id) : null,
        ["status"] = (q, v, _) => CommitmentStatus.All.Contains(v) ? q.Where(c => c.Status == v) : null,
        ["category"] = (q, v, _) => v.Trim() is { Length: > 0 } category ? q.Where(c => c.Category == category) : null,
        ["responsibleUserId"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } user ? q.Where(c => c.ResponsibleUserId == user) : null,
        ["kind"] = (q, v, _) => CommitmentKind.All.Contains(v) ? q.Where(c => c.Kind == v) : null,
        // Müşteriye göre (firma kartı): sözleşmenin müşterisi.
        ["partyId"] = (q, v, ctx) => FilterValue.Id(v) is { } party ? q.Where(c => ctx.Data.Set<Contract>().Any(k => k.Id == c.ContractId && k.PartyId == party)) : null,
    };

    protected override IOrderedQueryable<ContractCommitment> Order(IQueryable<ContractCommitment> source) => source.OrderBy(c => c.DueDate).ThenBy(c => c.Title);

    protected override IQueryable<ContractCommitment> Search(IQueryable<ContractCommitment> source, string term) =>
        source.Where(c => c.Title.Contains(term) || (c.Description != null && c.Description.Contains(term)));

    protected override IQueryable<ContractCommitment> ScopeToOwners(IQueryable<ContractCommitment> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var contracts = Context.Data.Set<Contract>();
        return source.Where(c => contracts.Any(k => k.Id == c.ContractId && ids.Contains(k.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        ContractChildren.ContractIdOf(record) is { } contractId
            ? ContractChildren.IsContractInScopeAsync(Context, contractId, owners, cancellationToken)
            : Task.FromResult(false);

    protected override async Task AppliedAsync(ContractCommitment entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var workflow = Context.Gate.IsOpen;

        if (workflow && !isNew)
        {
            // Gerçekleşme tamamlama/ihlal/muafiyet: iş akışı kapısı etkisi taahhütle aynı işlemde çalışır; tanım alanları doğrulanmaz.
            if (Context.Gate.CommitmentEffect is { } effect)
            {
                await effect(Context, entity, cancellationToken);
                entity.Touch();
            }

            return;
        }

        var contract = await data.Set<Contract>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == entity.ContractId, cancellationToken);
        if (contract is null || (await Context.Access.GetOwnersAsync(ContractsModule.Entities.Contract, cancellationToken) is { } owners && !owners.Contains(contract.OwnerId ?? Guid.Empty)))
        {
            throw new RecordValidationException([new RecordFieldError("contractId", "Sözleşme bulunamadı.")]);
        }

        var errors = new List<RecordFieldError>();
        if (contract.EffectiveStatus is EffectiveStatus.Terminated or EffectiveStatus.Cancelled or EffectiveStatus.Expired)
        {
            errors.Add(new RecordFieldError("contractId", "Süresi dolmuş, feshedilmiş ya da iptal edilmiş sözleşmeye taahhüt eklenemez ya da değiştirilemez."));
        }

        if (!isNew && entity.Status != CommitmentStatus.Active)
        {
            errors.Add(new RecordFieldError("status", "Tamamlanmış ya da iptal edilmiş taahhüt düzenlenemez."));
        }

        var occurrences = isNew ? [] : await data.Set<ContractCommitmentOccurrence>().Where(o => o.CommitmentId == entity.Id).ToListAsync(cancellationToken);
        var hasHistory = occurrences.Any(o => o.Status != OccurrenceStatus.Open);
        var after = ScheduleValues(entity);
        var changedKeys = isNew || scheduleBefore is null ? [.. LockedAfterHistory] : LockedAfterHistory.Where(k => scheduleBefore[k] != after[k]).ToList();
        var scheduleChanged = changedKeys.Count > 0;
        if (!isNew && hasHistory && scheduleChanged)
        {
            foreach (var name in changedKeys)
            {
                errors.Add(new RecordFieldError(name, "Gerçekleşmesi kaydedilmiş taahhütte takvim ve ölçüm değiştirilemez; önceki kayıtlar yeniden yazılmaz. Yeni bir taahhüt açın."));
            }
        }

        ValidateDefinition(entity, errors);
        await ValidateDependencyAsync(entity, errors, cancellationToken);

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (entity.Kind == CommitmentKind.OneTime)
        {
            entity.Recurrence = null;
            entity.RecurrenceEndDate = null;
        }

        if (string.IsNullOrWhiteSpace(entity.MetricName))
        {
            (entity.MetricName, entity.Unit, entity.Direction, entity.TargetValue, entity.Tolerance) = (null, null, null, null, null);
        }

        if (scheduleChanged && !hasHistory)
        {
            // Açık gerçekleşmeler (hiç kaydı olmayanlar) yeniden üretilir; kayıtlı gerçekleşme zaten bu noktaya ulaşamaz.
            data.Set<ContractCommitmentOccurrence>().RemoveRange(occurrences);
            ContractCommitmentWriter.Materialize(Context, entity, contract, [], Today());
        }

        if (isNew)
        {
            entity.CreatedBy = Context.CurrentUserId;
            entity.Status = CommitmentStatus.Active;
            ContractHistory.Add(Context, await data.Set<Contract>().SingleAsync(c => c.Id == entity.ContractId, cancellationToken), ContractEventKinds.CommitmentDefined, entity.Title);
        }
    }

    protected override async Task BeforeDeleteAsync(ContractCommitment entity, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var occurrences = await data.Set<ContractCommitmentOccurrence>().Where(o => o.CommitmentId == entity.Id).ToListAsync(cancellationToken);
        if (occurrences.Any(o => o.Status != OccurrenceStatus.Open))
        {
            throw new RecordConflictException("Gerçekleşmesi kaydedilmiş taahhüt silinemez (kayıtlar korunur); iptal edin.");
        }

        if (await data.Set<ContractCommitment>().AnyAsync(c => c.DependsOnCommitmentId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Başka bir taahhüt bu taahhüde bağlı; önce bağı kaldırın.");
        }

        data.Set<ContractCommitmentOccurrence>().RemoveRange(occurrences);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static void ValidateDefinition(ContractCommitment c, List<RecordFieldError> errors)
    {
        if (c.StartDate == default)
        {
            errors.Add(new RecordFieldError("startDate", "Başlangıç tarihi zorunludur."));
        }

        if (c.DueDate == default)
        {
            errors.Add(new RecordFieldError("dueDate", "Son tarih zorunludur."));
        }
        else if (c.StartDate != default && c.DueDate < c.StartDate)
        {
            errors.Add(new RecordFieldError("dueDate", "Son tarih başlangıçtan önce olamaz."));
        }

        if (c.Kind == CommitmentKind.Periodic)
        {
            if (c.Recurrence is null)
            {
                errors.Add(new RecordFieldError("recurrence", "Dönemsel taahhüt için tekrar kuralı seçin."));
            }

            if (c.RecurrenceEndDate is { } end && c.DueDate != default && end < c.DueDate)
            {
                errors.Add(new RecordFieldError("recurrenceEndDate", "Tekrar bitişi ilk son tarihten önce olamaz."));
            }
        }
        else if (c.Recurrence is not null || c.RecurrenceEndDate is not null)
        {
            errors.Add(new RecordFieldError("recurrence", "Tek seferlik taahhütte tekrar kuralı olmaz."));
        }

        if (!string.IsNullOrWhiteSpace(c.MetricName))
        {
            if (c.Direction is null)
            {
                errors.Add(new RecordFieldError("direction", "Ölçüm için yön (en az / en çok / tam) seçin."));
            }

            if (c.TargetValue is null)
            {
                errors.Add(new RecordFieldError("targetValue", "Ölçüm için hedef değer girin."));
            }
        }
        else if (c.Direction is not null || c.TargetValue is not null || c.Tolerance is not null || !string.IsNullOrWhiteSpace(c.Unit))
        {
            errors.Add(new RecordFieldError("metricName", "Hedef, tolerans, birim ya da yön girildiyse ölçüt adı da girilmelidir."));
        }
    }

    /// <summary>Bağımlılık aynı sözleşmeden olmalı, kendine işaret etmemeli ve döngü kurmamalıdır.</summary>
    private async Task ValidateDependencyAsync(ContractCommitment c, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        if (c.DependsOnCommitmentId is not { } dependsOn)
        {
            return;
        }

        if (dependsOn == c.Id)
        {
            errors.Add(new RecordFieldError("dependsOnCommitmentId", "Taahhüt kendine bağlı olamaz."));
            return;
        }

        var seen = new HashSet<Guid>();
        Guid? cursor = dependsOn;
        var first = true;
        while (cursor is { } id)
        {
            if (id == c.Id)
            {
                errors.Add(new RecordFieldError("dependsOnCommitmentId", "Bağımlılık döngü oluşturuyor."));
                return;
            }

            if (!seen.Add(id))
            {
                break;
            }

            var parent = await Context.Data.Set<ContractCommitment>().AsNoTracking().Where(x => x.Id == id).Select(x => new { x.ContractId, x.DependsOnCommitmentId }).SingleOrDefaultAsync(cancellationToken);
            if (parent is null)
            {
                break;
            }

            if (first && parent.ContractId != c.ContractId)
            {
                errors.Add(new RecordFieldError("dependsOnCommitmentId", "Bağımlı olunan taahhüt aynı sözleşmeden olmalıdır."));
                return;
            }

            first = false;
            cursor = parent.DependsOnCommitmentId;
        }
    }
}
