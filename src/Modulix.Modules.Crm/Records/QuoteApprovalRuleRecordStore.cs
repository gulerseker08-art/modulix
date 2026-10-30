using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Teklif iç onay kuralı kayıt deposu (ADR-0019, dilim 4). Okuma ve yazma <c>crm.quote-rules.manage</c> iznindedir; kural kayıtları
/// teklif değerlendirmesinde iç okuyucuyla okunur (izin gerekmez, çünkü kural bir sunucu kararıdır). Tüm tenant'a uygulanır (veri kapsamı yok).
/// </summary>
internal sealed class QuoteApprovalRuleRecordStore(CrmStoreContext context) : CrmRecordStore<QuoteApprovalRule>(context)
{
    public override string EntityKey => CrmModule.Entities.QuoteApprovalRule;

    protected override string ReadPermission => CrmModule.Permissions.QuoteRulesManage;

    protected override string WritePermission => CrmModule.Permissions.QuoteRulesManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<QuoteApprovalRule>> Fields { get; } =
    [
        Field.Text<QuoteApprovalRule>("name", QuoteApprovalRule.NameMaxLength, r => r.Name, (r, v) => r.Name = v!, required: true),
        Field.Text<QuoteApprovalRule>("description", QuoteApprovalRule.DescriptionMaxLength, r => r.Description, (r, v) => r.Description = v),
        Field.Count<QuoteApprovalRule>("priority", r => r.Priority, (r, v) => r.Priority = v),
        Field.Flag<QuoteApprovalRule>("isActive", r => r.IsActive, (r, v) => r.IsActive = v),
        Field.OptionalCurrency<QuoteApprovalRule>("currency", r => r.Currency, (r, v) => r.Currency = v),
        Field.Rate<QuoteApprovalRule>("minDiscountPercent", r => r.MinDiscountPercent, (r, v) => r.MinDiscountPercent = v),
        Field.Money<QuoteApprovalRule>("minGrossTotal", r => r.MinGrossTotal, (r, v) => r.MinGrossTotal = v),
        Field.Owner<QuoteApprovalRule>("approverId", r => r.ApproverId, (r, v) => r.ApproverId = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<QuoteApprovalRule>> Filters { get; } =
        new Dictionary<string, RecordFilter<QuoteApprovalRule>>
        {
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(r => r.IsActive == active) : null,
        };

    protected override IOrderedQueryable<QuoteApprovalRule> Order(IQueryable<QuoteApprovalRule> source) => source.OrderBy(r => r.Priority).ThenBy(r => r.Name);

    protected override IQueryable<QuoteApprovalRule> Search(IQueryable<QuoteApprovalRule> source, string term) => source.Where(r => r.Name.Contains(term));

    protected override Task AppliedAsync(QuoteApprovalRule entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.MinDiscountPercent is null && entity.MinGrossTotal is null)
        {
            errors.Add(new RecordFieldError("minDiscountPercent", "En az bir koşul girin: indirim yüzdesi ya da genel toplam."));
        }

        if (entity.MinDiscountPercent is { } percent && (percent < 0 || percent >= 100))
        {
            errors.Add(new RecordFieldError("minDiscountPercent", "İndirim eşiği 0 ile 100 arasında (100 hariç) olmalıdır."));
        }

        if (entity.MinGrossTotal is < 0)
        {
            errors.Add(new RecordFieldError("minGrossTotal", "Tutar eşiği negatif olamaz."));
        }

        // Para birimi yalnızca tutar koşuluyla anlamlıdır; tutar yoksa boşaltılır.
        if (entity.MinGrossTotal is null)
        {
            entity.Currency = null;
        }

        return errors.Count > 0 ? Task.FromException(new RecordValidationException(errors)) : Task.CompletedTask;
    }
}
