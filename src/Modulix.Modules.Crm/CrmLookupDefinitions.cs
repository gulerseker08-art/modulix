using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM'in lookup alanları (ADR-0016): kullanıcının seçtiği N→1 referanslar. Teknik tanımlar kodda sabittir; firma yalnızca
/// "aranabilir" ayarını değiştirir. Hedefe erişim kayıt boru hattının kurallarıyla (izin, veri kapsamı) belirlenir.
/// Lookup olmayanlar: sistem alanları (sourceLeadId, convertedXxxId, createdBy, previousActivityId), kişi-firma ilişkisinin
/// birincil firma eşitlemesi, Lead'e aktivite bağı (üst kayıtta sabit) ve çoktan çoğa ilişkiler (kişi↔firma, aktivite katılımcıları).
/// </summary>
public static class CrmLookupDefinitions
{
    public const string ContactCompany = "crm.contact.company";
    public const string OpportunityCompany = "crm.opportunity.company";
    public const string OpportunityContact = "crm.opportunity.contact";
    public const string LeadCompany = "crm.lead.company";
    public const string LeadContact = "crm.lead.contact";
    public const string ActivityCompany = "crm.activity.company";
    public const string ActivityContact = "crm.activity.contact";
    public const string ActivityOpportunity = "crm.activity.opportunity";
    public const string LocationContact = "crm.location.contact";
    public const string CompanyContactContact = "crm.company-contact.contact";
    public const string CompanyRelationChild = "crm.company-relation.child";
    public const string CompanyOwner = "crm.company.owner";
    public const string ContactOwner = "crm.contact.owner";
    public const string OpportunityOwner = "crm.opportunity.owner";
    public const string LeadOwner = "crm.lead.owner";
    public const string ActivityOwner = "crm.activity.owner";
    public const string QuoteCompany = "crm.quote.company";
    public const string QuoteContact = "crm.quote.contact";
    public const string QuoteOpportunity = "crm.quote.opportunity";
    public const string QuotePriceList = "crm.quote.price-list";
    public const string QuoteOwner = "crm.quote.owner";
    public const string QuoteLineProduct = "crm.quote-line.product";
    public const string QuoteRuleApprover = "crm.quote-approval-rule.approver";
    public const string QuoteDelegationFrom = "crm.quote-approval-delegation.from";
    public const string QuoteDelegationTo = "crm.quote-approval-delegation.to";

    private static readonly string[] CompanyLabel = ["name"];
    private static readonly string[] CompanySecondary = ["city", "taxNumber"];
    private static readonly string[] ContactLabel = ["firstName", "lastName"];
    private static readonly string[] ContactSecondary = ["title", "email"];

    private static LookupDefinition Company(string key, string source, string field, string title, bool defaultSearchable = true) =>
        new(key, source, field, title, LookupTargetKind.Record, CrmModule.Entities.Company)
        {
            LabelFields = CompanyLabel,
            SecondaryFields = CompanySecondary,
            SearchDescription = "Ad, vergi numarası, şehir, etiket, iletişim noktaları ve adresler",
            ViewPath = "crm/firmalar/{id}",
            DefaultSearchable = defaultSearchable,
        };

    private static LookupDefinition Contact(string key, string source, string field, string title, string? dependsOn = "companyId") =>
        new(key, source, field, title, LookupTargetKind.Record, CrmModule.Entities.Contact)
        {
            LabelFields = ContactLabel,
            SecondaryFields = ContactSecondary,
            SearchDescription = "Ad, soyad, e-posta ve iletişim noktaları",
            ViewPath = "crm/kisiler/{id}",
            DependsOnField = dependsOn,
            DependsOnFilter = dependsOn is null ? null : "relatedCompanyId",
            DependsOnHint = dependsOn is null ? null : "Firma seçiliyse kişiler o firmayla sınırlanır.",
        };

    private static LookupDefinition Owner(string key, string source, string title) =>
        new(key, source, "ownerId", title, LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        };

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        Company(ContactCompany, CrmModule.Entities.Contact, "companyId", "Firma"),
        Company(OpportunityCompany, CrmModule.Entities.Opportunity, "companyId", "Firma"),
        Contact(OpportunityContact, CrmModule.Entities.Opportunity, "contactId", "Kişi"),
        Company(LeadCompany, CrmModule.Entities.Lead, "companyId", "Mevcut firma"),
        Contact(LeadContact, CrmModule.Entities.Lead, "contactId", "Mevcut kişi"),
        Company(ActivityCompany, CrmModule.Entities.Activity, "companyId", "Firma"),
        Contact(ActivityContact, CrmModule.Entities.Activity, "contactId", "Kişi"),
        new(ActivityOpportunity, CrmModule.Entities.Activity, "opportunityId", "Fırsat", LookupTargetKind.Record, CrmModule.Entities.Opportunity)
        {
            LabelFields = ["title"],
            SecondaryFields = ["stage"],
            SearchDescription = "Fırsat başlığı",
            ViewPath = "crm/firsatlar",
            DependsOnField = "companyId",
            DependsOnFilter = "companyId",
            DependsOnHint = "Firma seçiliyse fırsatlar o firmayla sınırlanır.",
        },
        Contact(LocationContact, CrmModule.Entities.Location, "contactId", "Yetkili kişi"),
        Contact(CompanyContactContact, CrmModule.Entities.CompanyContact, "contactId", "Kişi", dependsOn: null),
        Company(CompanyRelationChild, CrmModule.Entities.CompanyRelation, "childCompanyId", "Bağlı firma"),
        Owner(CompanyOwner, CrmModule.Entities.Company, "Sorumlu"),
        Owner(ContactOwner, CrmModule.Entities.Contact, "Sorumlu"),
        Owner(OpportunityOwner, CrmModule.Entities.Opportunity, "Sorumlu"),
        Owner(LeadOwner, CrmModule.Entities.Lead, "Sorumlu"),
        Owner(ActivityOwner, CrmModule.Entities.Activity, "Sorumlu"),
        Company(QuoteCompany, CrmModule.Entities.Quote, "companyId", "Firma"),
        Contact(QuoteContact, CrmModule.Entities.Quote, "contactId", "Kişi"),
        new(QuoteOpportunity, CrmModule.Entities.Quote, "opportunityId", "Fırsat", LookupTargetKind.Record, CrmModule.Entities.Opportunity)
        {
            LabelFields = ["title"],
            SecondaryFields = ["stage"],
            SearchDescription = "Fırsat başlığı",
            ViewPath = "crm/firsatlar",
            DependsOnField = "companyId",
            DependsOnFilter = "companyId",
            DependsOnHint = "Firma seçiliyse fırsatlar o firmayla sınırlanır.",
        },
        new(QuotePriceList, CrmModule.Entities.Quote, "priceListId", "Fiyat listesi", LookupTargetKind.Record, CrmModule.Entities.PriceList)
        {
            LabelFields = ["name"],
            SecondaryFields = ["currency"],
            SearchDescription = "Fiyat listesi adı ve segment",
            ViewPath = "crm/fiyat-listeleri",
        },
        Owner(QuoteOwner, CrmModule.Entities.Quote, "Sorumlu"),
        new(QuoteRuleApprover, CrmModule.Entities.QuoteApprovalRule, "approverId", "Onaylayan", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(QuoteDelegationFrom, CrmModule.Entities.QuoteApprovalDelegation, "fromUserId", "Vekâlet veren", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(QuoteDelegationTo, CrmModule.Entities.QuoteApprovalDelegation, "toUserId", "Vekil", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(QuoteLineProduct, CrmModule.Entities.QuoteLine, "productId", "Ürün", LookupTargetKind.Record, CrmModule.Entities.Product)
        {
            LabelFields = ["name"],
            SecondaryFields = ["code"],
            SearchDescription = "Ürün kodu ve adı",
            ViewPath = "crm/urunler",
        },
    ];
}
