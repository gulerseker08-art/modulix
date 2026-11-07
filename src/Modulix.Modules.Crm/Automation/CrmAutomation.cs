using System.Linq.Expressions;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Automation;

/// <summary>
/// CRM'in kural altyapısı yetenek beyanı (ADR-0027, Dilim 3): müşteri adayı ve fırsat için olaylar, koşulda kullanılabilen alanlar, aktivite
/// ilişkisi, tarama kaynağı ve ilk aksiyon. Alanların ve tarih sütunlarının tek kaynağı bu sınıftır; tarama kaynağı beyan edilen her tarih
/// alanını destekler (birim testle denetlenir).
/// <para>
/// Hassas alanlar (<c>SensitiveFields</c>) aynı izinle <see cref="AutomationFieldDefinition.ReadPermission"/> olarak da beyan edilir: tutar ve
/// olasılık, "fırsat tutarı" iznine sahip olmayan kimlik için koşulda yanlış değerlenir ve kural yazarı o alana koşul koyamaz.
/// </para>
/// </summary>
public static class CrmAutomation
{
    public const string FollowUpAction = "crm.follow-up.create";

    public const string LeadActivities = "crm.lead.activities";

    public const string OpportunityActivities = "crm.opportunity.activities";

    /// <summary>Etkinliği olan kayıtların tarih alanları: alan anahtarı → tarama sütunu. Beyan ve kaynak aynı tabloyu kullanır.</summary>
    internal static IReadOnlyDictionary<string, Expression<Func<Lead, DateOnly?>>> LeadDateColumns { get; } =
        new Dictionary<string, Expression<Func<Lead, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["targetPurchaseDate"] = l => l.TargetPurchaseDate,
        };

    internal static IReadOnlyDictionary<string, Expression<Func<Opportunity, DateOnly?>>> OpportunityDateColumns { get; } =
        new Dictionary<string, Expression<Func<Opportunity, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["expectedCloseDate"] = o => o.ExpectedCloseDate,
            ["nextActionDate"] = o => o.NextActionDate,
            ["targetDecisionDate"] = o => o.TargetDecisionDate,
        };

    /// <summary>Aktivite durumlarının Türkçe adları (ilişki koşulunda seçenek).</summary>
    internal static IReadOnlyList<AutomationOption> ActivityStatusOptions { get; } =
    [
        new(ActivityStatus.Open, "Açık"),
        new(ActivityStatus.InProgress, "Sürüyor"),
        new(ActivityStatus.Blocked, "Engelli"),
        new(ActivityStatus.Completed, "Tamamlandı"),
        new(ActivityStatus.NoAnswer, "Yanıt yok"),
        new(ActivityStatus.NoShow, "Gelmedi"),
        new(ActivityStatus.Cancelled, "İptal"),
    ];

    internal static IReadOnlyList<AutomationOption> PriorityOptions { get; } =
    [
        new(LeadPriority.Low, "Düşük"),
        new(LeadPriority.Normal, "Normal"),
        new(LeadPriority.High, "Yüksek"),
        new(LeadPriority.Urgent, "Acil"),
    ];

    private static AutomationFieldDefinition Date(string entity, string name, string label, string hint) =>
        new(entity, name, label, AutomationFieldType.Date) { Hint = hint };

    private static IEnumerable<AutomationFieldDefinition> ActivityTargetFields() =>
    [
        new(CrmModule.Entities.Activity, "status", "Aktivite durumu", AutomationFieldType.Choice) { Options = ActivityStatusOptions },
    ];

    public static AutomationCapabilities Capabilities { get; } = new()
    {
        Entities =
        [
            new(CrmModule.Entities.Lead, "Müşteri adayı", CrmModule.Permissions.LeadsRead) { RecordSource = typeof(CrmAutomationRecordSource), LabelField = "subject" },
            new(CrmModule.Entities.Opportunity, "Fırsat", CrmModule.Permissions.OpportunitiesRead) { RecordSource = typeof(CrmAutomationRecordSource), LabelField = "title" },
        ],
        Events =
        [
            new("crm.lead.created", CrmModule.Entities.Lead, DomainEventTypes.RecordCreated, "Müşteri adayı oluşturuldu"),
            new("crm.lead.updated", CrmModule.Entities.Lead, DomainEventTypes.RecordUpdated, "Müşteri adayı güncellendi"),
            new("crm.opportunity.created", CrmModule.Entities.Opportunity, DomainEventTypes.RecordCreated, "Fırsat oluşturuldu"),
            new("crm.opportunity.updated", CrmModule.Entities.Opportunity, DomainEventTypes.RecordUpdated, "Fırsat güncellendi"),
        ],
        Fields =
        [
            // Müşteri adayı
            new(CrmModule.Entities.Lead, "subject", "Konu", AutomationFieldType.Text),
            new(CrmModule.Entities.Lead, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(LeadStatus.New, "Yeni"), new(LeadStatus.Contacting, "İletişimde"), new(LeadStatus.Qualified, "Nitelikli"),
                    new(LeadStatus.Nurturing, "Beslemede"), new(LeadStatus.Disqualified, "Elendi"), new(LeadStatus.Converted, "Dönüştürüldü"),
                ],
            },
            new(CrmModule.Entities.Lead, "priority", "Öncelik", AutomationFieldType.Choice) { Options = PriorityOptions },
            new(CrmModule.Entities.Lead, "ownerId", "Sorumlu", AutomationFieldType.Lookup) { LookupKey = CrmLookupDefinitions.LeadOwner },
            new(CrmModule.Entities.Lead, "estimatedBudget", "Tahmini bütçe", AutomationFieldType.Money) { CurrencyField = "currency" },
            Date(CrmModule.Entities.Lead, "targetPurchaseDate", "Hedef satın alma tarihi", "Satın alma tarihi yaklaşan müşteri adayları için (tarihe kala tetikleyicisi)."),

            // Fırsat
            new(CrmModule.Entities.Opportunity, "title", "Başlık", AutomationFieldType.Text),
            new(CrmModule.Entities.Opportunity, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(OpportunityStatus.Open, "Açık"), new(OpportunityStatus.OnHold, "Beklemede"), new(OpportunityStatus.Won, "Kazanıldı"),
                    new(OpportunityStatus.Lost, "Kaybedildi"), new(OpportunityStatus.Cancelled, "İptal"),
                ],
            },
            new(CrmModule.Entities.Opportunity, "stage", "Aşama", AutomationFieldType.Choice)
            {
                Hint = "Aşama anahtarı firmanın seçim listesindeki kalıcı anahtardır (görünen ad değil).",
            },
            new(CrmModule.Entities.Opportunity, "priority", "Öncelik", AutomationFieldType.Choice) { Options = PriorityOptions },
            new(CrmModule.Entities.Opportunity, "ownerId", "Sorumlu", AutomationFieldType.Lookup) { LookupKey = CrmLookupDefinitions.OpportunityOwner },
            new(CrmModule.Entities.Opportunity, "companyId", "Firma", AutomationFieldType.Lookup) { LookupKey = CrmLookupDefinitions.OpportunityCompany },
            new(CrmModule.Entities.Opportunity, "amount", "Tutar", AutomationFieldType.Money)
            {
                CurrencyField = "currency",
                ReadPermission = CrmModule.Permissions.OpportunityAmountsRead,
                Hint = "Tutar izni olmayanlar bu alana koşul koyamaz; farklı para birimleri karşılaştırılmaz.",
            },
            new(CrmModule.Entities.Opportunity, "probability", "Olasılık (%)", AutomationFieldType.Number)
            {
                ReadPermission = CrmModule.Permissions.OpportunityAmountsRead,
            },
            Date(CrmModule.Entities.Opportunity, "expectedCloseDate", "Beklenen kapanış tarihi", "Kapanış tarihi yaklaşan ya da geçen fırsatlar için."),
            Date(CrmModule.Entities.Opportunity, "nextActionDate", "Sonraki adım tarihi", "Sonraki adımı gecikmiş fırsatlar için."),
            Date(CrmModule.Entities.Opportunity, "targetDecisionDate", "Hedef karar tarihi", "Karar tarihi yaklaşan fırsatlar için."),
        ],
        Relations =
        [
            new(LeadActivities, CrmModule.Entities.Lead, CrmModule.Entities.Activity, "Aktiviteler", [.. ActivityTargetFields()], typeof(CrmActivityRelationResolver))
            {
                ReadPermission = CrmModule.Permissions.ActivitiesRead,
            },
            new(OpportunityActivities, CrmModule.Entities.Opportunity, CrmModule.Entities.Activity, "Aktiviteler", [.. ActivityTargetFields()], typeof(CrmActivityRelationResolver))
            {
                ReadPermission = CrmModule.Permissions.ActivitiesRead,
            },
        ],
        Actions =
        [
            AutomationActionRegistration.For<CreateFollowUpAction>(new AutomationActionDefinition(
                FollowUpAction,
                "Takip aktivitesi oluştur",
                "Kayda bağlı bir takip aktivitesi açar. Atanan verilmezse kaydın sorumlusuna atanır. Aynı kural aynı kayıt için bir kez açar.",
                [
                    new AutomationParameter("subject", "Konu", AutomationFieldType.Text)
                    {
                        MaxLength = 200,
                        Description = "Kayıt değişkeni için {{alan}} yazılabilir (ör. {{crm.lead.subject}}).",
                    },
                    new AutomationParameter("description", "Açıklama", AutomationFieldType.Text, Required: false)
                    {
                        MaxLength = 2000,
                        Description = "Kayıt değişkeni için {{alan}} yazılabilir.",
                    },
                    new AutomationParameter("dueInDays", "Kaç gün sonra (bugünden)", AutomationFieldType.Number, Required: false)
                    {
                        Description = "Boşsa 1 gün sonra. 0-365.",
                        AllowFieldBinding = false,
                    },
                    new AutomationParameter("assigneeId", "Atanacak kullanıcı", AutomationFieldType.Lookup, Required: false)
                    {
                        Description = "Boşsa kaydın sorumlusu.",
                        LookupKey = CrmLookupDefinitions.ActivityOwner,
                        AllowFieldBinding = false,
                    },
                ],
                [CrmModule.Permissions.ActivitiesWrite],
                [CrmModule.ModuleKey],
                AutomationSideEffect.Write,
                SupportsDryRun: true)),
        ],
        Templates = CrmAutomationTemplates.All,
    };
}
