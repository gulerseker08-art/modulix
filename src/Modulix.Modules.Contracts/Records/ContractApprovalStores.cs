using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme iç onay kuralı kayıt deposu (ADR-0022, dilim 5). Okuma ve yazma <c>contracts.approvals.manage</c> iznindedir (kuralı yönetici tanımlar,
/// kural eşikleri onaylayanlar dahil herkese açık değildir); kurallar onaya gönderimde iç okuyucuyla okunur. Tüm tenant'a uygulanır (veri kapsamı yok).
/// Kural sürümü (<c>Version</c>) onaya gönderim planına yazılır; kural sonradan değişse de bekleyen onayın planı değişmez.
/// </summary>
internal sealed partial class ContractApprovalRuleRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractApprovalRule>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractApprovalRule;

    protected override string ReadPermission => ContractsModule.Permissions.ApprovalsManage;

    protected override string WritePermission => ContractsModule.Permissions.ApprovalsManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ContractApprovalRule>> Fields { get; } =
    [
        Field.Text<ContractApprovalRule>("name", ContractApprovalRule.NameMaxLength, r => r.Name, (r, v) => r.Name = v!, required: true),
        Field.Text<ContractApprovalRule>("description", ContractApprovalRule.DescriptionMaxLength, r => r.Description, (r, v) => r.Description = v),
        Field.Integer<ContractApprovalRule>("priority", r => r.Priority, (r, v) => r.Priority = v ?? 0, max: 9999),
        Field.Integer<ContractApprovalRule>("step", r => r.Step, (r, v) => r.Step = v ?? 1),
        Field.Flag<ContractApprovalRule>("isActive", r => r.IsActive, (r, v) => r.IsActive = v),
        Field.ListChoice<ContractApprovalRule>("contractType", ContractsModule.Lists.ContractType, r => r.ContractType, (r, v) => r.ContractType = v, required: false),
        Field.Text<ContractApprovalRule>("currency", Contract.CurrencyLength, r => r.Currency, (r, v) => r.Currency = v),
        Field.Money<ContractApprovalRule>("minGrossTotal", r => r.MinGrossTotal, (r, v) => r.MinGrossTotal = v),
        Field.Rate<ContractApprovalRule>("minDiscountPercent", r => r.MinDiscountPercent, (r, v) => r.MinDiscountPercent = v),
        Field.Integer<ContractApprovalRule>("minTermMonths", r => r.MinTermMonths, (r, v) => r.MinTermMonths = v, max: 1200),
        Field.Flag<ContractApprovalRule>("requiresNonStandardTerms", r => r.RequiresNonStandardTerms, (r, v) => r.RequiresNonStandardTerms = v),
        Field.OptionalChoice<ContractApprovalRule>("minRiskLevel", RiskLevel.All, r => r.MinRiskLevel, (r, v) => r.MinRiskLevel = v),
        Field.Owner<ContractApprovalRule>("approverId", r => r.ApproverId, (r, v) => r.ApproverId = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractApprovalRule>> Filters { get; } =
        new Dictionary<string, RecordFilter<ContractApprovalRule>>
        {
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(r => r.IsActive == active) : null,
        };

    protected override IOrderedQueryable<ContractApprovalRule> Order(IQueryable<ContractApprovalRule> source) => source.OrderBy(r => r.Step).ThenBy(r => r.Priority).ThenBy(r => r.Name);

    protected override IQueryable<ContractApprovalRule> Search(IQueryable<ContractApprovalRule> source, string term) => source.Where(r => r.Name.Contains(term));

    protected override Task AppliedAsync(ContractApprovalRule entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (!ContractApprovalEvaluator.HasCriterion(entity.MinGrossTotal, entity.MinDiscountPercent, entity.MinTermMonths, entity.RequiresNonStandardTerms, entity.MinRiskLevel))
        {
            errors.Add(new RecordFieldError("minGrossTotal", "En az bir koşul girin: tutar, indirim, süre, standart dışı madde ya da risk düzeyi."));
        }

        if (entity.MinDiscountPercent is { } percent && (percent < 0 || percent >= 100))
        {
            errors.Add(new RecordFieldError("minDiscountPercent", "İndirim eşiği 0 ile 100 arasında (100 hariç) olmalıdır."));
        }

        if (entity.MinGrossTotal is < 0)
        {
            errors.Add(new RecordFieldError("minGrossTotal", "Tutar eşiği negatif olamaz."));
        }

        if (entity.MinTermMonths is < 0)
        {
            errors.Add(new RecordFieldError("minTermMonths", "Süre eşiği negatif olamaz."));
        }

        if (entity.Step is < 1 or > 9)
        {
            errors.Add(new RecordFieldError("step", "Onay adımı 1 ile 9 arasında olmalıdır."));
        }

        if (entity.Currency is { Length: > 0 } currency)
        {
            if (CurrencyCode().IsMatch(currency.Trim().ToUpperInvariant()))
            {
                entity.Currency = currency.Trim().ToUpperInvariant();
            }
            else
            {
                errors.Add(new RecordFieldError("currency", "Para birimi üç harfli kod olmalıdır (ör. TRY)."));
            }
        }

        // Para birimi yalnızca tutar koşuluyla anlamlıdır; tutar yoksa boşaltılır.
        if (entity.MinGrossTotal is null)
        {
            entity.Currency = null;
        }

        return errors.Count > 0 ? Task.FromException(new RecordValidationException(errors)) : Task.CompletedTask;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}

/// <summary>
/// Sözleşme onay vekâleti kayıt deposu. Okuma ve yazma <c>contracts.approvals.manage</c> iznindedir (vekâleti yönetici tanımlar); onay anında vekâlet
/// iç okuyucuyla denetlenir. Tüm tenant'a uygulanır.
/// </summary>
internal sealed class ContractApprovalDelegationRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractApprovalDelegation>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractApprovalDelegation;

    protected override string ReadPermission => ContractsModule.Permissions.ApprovalsManage;

    protected override string WritePermission => ContractsModule.Permissions.ApprovalsManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ContractApprovalDelegation>> Fields { get; } =
    [
        Field.Owner<ContractApprovalDelegation>("fromUserId", d => d.FromUserId == Guid.Empty ? null : d.FromUserId, (d, v) => d.FromUserId = v ?? Guid.Empty),
        Field.Owner<ContractApprovalDelegation>("toUserId", d => d.ToUserId == Guid.Empty ? null : d.ToUserId, (d, v) => d.ToUserId = v ?? Guid.Empty),
        Field.Time<ContractApprovalDelegation>("startsAt", d => d.StartsAt == default ? null : d.StartsAt, (d, v) => { if (v is { } t) { d.StartsAt = t; } }),
        Field.Time<ContractApprovalDelegation>("endsAt", d => d.EndsAt, (d, v) => d.EndsAt = v),
        Field.Flag<ContractApprovalDelegation>("isActive", d => d.IsActive, (d, v) => d.IsActive = v),
        Field.Text<ContractApprovalDelegation>("note", ContractApprovalDelegation.NoteMaxLength, d => d.Note, (d, v) => d.Note = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractApprovalDelegation>> Filters { get; } =
        new Dictionary<string, RecordFilter<ContractApprovalDelegation>>
        {
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(d => d.IsActive == active) : null,
            ["fromUserId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(d => d.FromUserId == id) : null,
            ["toUserId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(d => d.ToUserId == id) : null,
        };

    protected override IOrderedQueryable<ContractApprovalDelegation> Order(IQueryable<ContractApprovalDelegation> source) =>
        source.OrderByDescending(d => d.IsActive).ThenBy(d => d.StartsAt);

    protected override IQueryable<ContractApprovalDelegation> Search(IQueryable<ContractApprovalDelegation> source, string term) =>
        source.Where(d => d.Note != null && d.Note.Contains(term));

    protected override Task AppliedAsync(ContractApprovalDelegation entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.FromUserId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("fromUserId", "Vekâlet veren zorunludur."));
        }

        if (entity.ToUserId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("toUserId", "Vekil zorunludur."));
        }

        if (entity.FromUserId != Guid.Empty && entity.FromUserId == entity.ToUserId)
        {
            errors.Add(new RecordFieldError("toUserId", "Kişi kendine vekâlet veremez."));
        }

        if (entity.StartsAt == default)
        {
            entity.StartsAt = DateTimeOffset.UtcNow;
        }

        if (entity.EndsAt is { } end && end <= entity.StartsAt)
        {
            errors.Add(new RecordFieldError("endsAt", "Bitiş, başlangıçtan sonra olmalıdır."));
        }

        return errors.Count > 0 ? Task.FromException(new RecordValidationException(errors)) : Task.CompletedTask;
    }
}
