using Modulix.Core.Contracts.Reporting;

namespace Modulix.Modules.Crm.Reporting;

/// <summary>
/// CRM'in ortak rapor merkezine katkısı (ADR-0026, dilim 3): fırsat ve lead veri kümeleri ile satış raporu ekranlarına bağlantılar. Veri kümeleri kayıt
/// boru hattından okunur (izin, veri kapsamı, hassas alan kuralları fırsat/lead kayıt türünündür); tutar ve olasılık sütunları
/// <c>crm.opportunity-amounts.read</c> izni olmayan için hiç görünmez. Tutar sütunları para birimine göre ayrı toplanır. Satış genel görünümü,
/// hedef ve tahmin gibi kurallı metrikler (kur, dönem, kohort) bu veri kümelerinden değil, kendi ekranlarından okunur.
/// </summary>
internal static class CrmReportDatasets
{
    public const string Opportunities = "crm.opportunities";
    public const string Leads = "crm.leads";

    private static readonly IReadOnlyList<DatasetOption> OpportunityStatuses =
    [
        new("Open", "Açık"), new("OnHold", "Beklemede"), new("Won", "Kazanıldı"), new("Lost", "Kaybedildi"), new("Cancelled", "İptal"),
    ];

    private static readonly IReadOnlyList<DatasetOption> LeadStatuses =
    [
        new("New", "Yeni"), new("Contacting", "İletişimde"), new("Qualified", "Nitelikli"), new("Nurturing", "Beslemede"),
        new("Disqualified", "Elendi"), new("Converted", "Dönüştürüldü"),
    ];

    private static readonly IReadOnlyList<DatasetOption> Priorities =
        [new("Low", "Düşük"), new("Normal", "Normal"), new("High", "Yüksek"), new("Urgent", "Acil")];

    private const ColumnUse All = ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show;
    private const ColumnUse FilterShow = ColumnUse.Filter | ColumnUse.Show;

    public static ModuleReporting Reporting { get; } = new(
        [OpportunityDataset(), LeadDataset()],
        [
            new("crm.reports.sales-overview", "Satış görünümü",
                "Gerçekleşen, açık pipeline, ağırlıklı tahmin ve hedefe göre durum; kart toplamları detay listesiyle eşleşir.", "crm/satis-gorunumu", CrmModule.Permissions.SalesReportsRead),
            new("crm.reports.sales-targets", "Satış hedefleri", "Dönem, metrik ve kapsama göre hedefler, sürümler ve gerçekleşme.", "crm/satis-hedefleri", CrmModule.Permissions.SalesTargetsRead),
            new("crm.reports.sales-forecasts", "Satış tahmini", "Sürümlü tahmin çalışma alanı: kategori, düzeltme, sunma, kilit ve karşılaştırma.", "crm/satis-tahmini", CrmModule.Permissions.ForecastsRead),
            new("crm.reports.sales-analysis", "Satış analizi", "Teklif ve lead kohortları, aşama dönüşümü.", "crm/satis-analizi", CrmModule.Permissions.SalesReportsRead),
            new("crm.reports.sales-team", "Ekip performansı", "Kullanıcı bazında satış, kazanma oranı, iş yükü ve döngü süresi.", "crm/ekip-performansi", CrmModule.Permissions.SalesReportsRead),
            new("crm.reports.sales-data-quality", "Veri kalitesi", "Eksik tutar, kur, olasılık ve kapanış tarihi; geciken ve hareketsiz açık fırsatlar.", "crm/veri-kalitesi", CrmModule.Permissions.SalesReportsRead),
        ]);

    private static DatasetDefinition OpportunityDataset() => new(
        Opportunities, "Fırsatlar", CrmModule.Entities.Opportunity, 1,
        [
            new("opportunityNumber", "Fırsat no", DatasetColumnKind.Text, FilterShow),
            new("title", "Başlık", DatasetColumnKind.Text, FilterShow),
            new("stage", "Aşama", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.OpportunityStage, StoreFilter = "stage" },
            new("status", "Durum", DatasetColumnKind.Choice, All) { Options = OpportunityStatuses, StoreFilter = "status" },
            new("priority", "Öncelik", DatasetColumnKind.Choice, All) { Options = Priorities, StoreFilter = "priority" },
            new("type", "Tür", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.OpportunityType, StoreFilter = "type" },
            new("source", "Kaynak", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.LeadSource, StoreFilter = "source" },
            new("lossReason", "Kayıp nedeni", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.OpportunityLossReason, StoreFilter = "lossReason" },
            new("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.OpportunityOwner, StoreFilter = "ownerId" },
            new("companyId", "Firma", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.OpportunityCompany, StoreFilter = "companyId" },
            new("amount", "Tutar", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "currency" },
            new("wonAmount", "Kazanılan tutar", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "wonCurrency" },
            new("currency", "Para birimi", DatasetColumnKind.Text, All) { StoreFilter = "currency" },
            new("probability", "Olasılık (%)", DatasetColumnKind.Number, ColumnUse.Measure | FilterShow),
            new("expectedCloseDate", "Beklenen kapanış", DatasetColumnKind.Date, All) { StoreFilterFrom = "closeFrom", StoreFilterTo = "closeTo" },
            new("actualCloseDate", "Gerçek kapanış", DatasetColumnKind.Date, All) { StoreFilterFrom = "actualCloseFrom", StoreFilterTo = "actualCloseTo" },
            new("nextActionDate", "Sonraki aksiyon", DatasetColumnKind.Date, All),
        ])
    {
        AllowsCustomFields = true,
        DefaultColumns = ["opportunityNumber", "title", "stage", "status", "ownerId", "amount", "expectedCloseDate"],
        RecordPath = "crm/firsatlar/{id}",
        ReadyReports =
        [
            new("crm.opportunities.open-by-stage", "Aşamaya göre açık fırsatlar", "Açık fırsatların adedi ve tutarı, aşama bazında (para birimine göre ayrı).",
                new(Opportunities, [], [new("status", FilterOperator.Equals, "Open")], [new("stage")], [new(null, Aggregation.Count), new("amount", Aggregation.Sum)],
                    new("count", SortDirection.Descending), ChartKind.Bar)),
            new("crm.opportunities.expected-by-month", "Aylık beklenen kapanış", "Açık fırsatların beklenen kapanış ayına göre adedi ve tutarı.",
                new(Opportunities, [], [new("status", FilterOperator.Equals, "Open")], [new("expectedCloseDate", DateGrain.Month)],
                    [new(null, Aggregation.Count), new("amount", Aggregation.Sum)], new("expectedCloseDate"), ChartKind.Line)),
            new("crm.opportunities.won-by-owner", "Sorumluya göre kazanılan fırsatlar", "Kazanılan fırsatların sorumlu bazında adedi ve kazanılan tutarı.",
                new(Opportunities, [], [new("status", FilterOperator.Equals, "Won")], [new("ownerId")], [new(null, Aggregation.Count), new("wonAmount", Aggregation.Sum)],
                    new("count", SortDirection.Descending), ChartKind.Bar)),
            new("crm.opportunities.loss-reasons", "Kayıp nedenleri", "Kaybedilen fırsatların nedenlerine göre dağılımı.",
                new(Opportunities, [], [new("status", FilterOperator.Equals, "Lost")], [new("lossReason")], [new(null, Aggregation.Count)],
                    new("count", SortDirection.Descending), ChartKind.Pie)),
            new("crm.opportunities.list", "Açık fırsat listesi", "Açık fırsatların tablosu.",
                new(Opportunities, ["opportunityNumber", "title", "stage", "ownerId", "amount", "expectedCloseDate"], [new("status", FilterOperator.Equals, "Open")], [], [],
                    new("expectedCloseDate"))),
        ],
    };

    private static DatasetDefinition LeadDataset() => new(
        Leads, "Lead'ler", CrmModule.Entities.Lead, 1,
        [
            new("leadNumber", "Lead no", DatasetColumnKind.Text, FilterShow),
            new("subject", "Konu", DatasetColumnKind.Text, FilterShow),
            new("status", "Durum", DatasetColumnKind.Choice, All) { Options = LeadStatuses, StoreFilter = "status" },
            new("priority", "Öncelik", DatasetColumnKind.Choice, All) { Options = Priorities, StoreFilter = "priority" },
            new("source", "Kaynak", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.LeadSource, StoreFilter = "source" },
            new("disqualificationReason", "Eleme nedeni", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.LeadDisqualificationReason },
            new("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.LeadOwner, StoreFilter = "ownerId" },
            new("companyName", "Firma adı", DatasetColumnKind.Text, FilterShow),
            new("industry", "Sektör", DatasetColumnKind.Text, All),
            new("country", "Ülke", DatasetColumnKind.Text, All),
            new("city", "Şehir", DatasetColumnKind.Text, All),
            new("utmCampaign", "Kampanya (UTM)", DatasetColumnKind.Text, All),
            new("firstReceivedAt", "Alınma zamanı", DatasetColumnKind.DateTime, All),
            new("convertedAt", "Dönüşüm zamanı", DatasetColumnKind.DateTime, All),
        ])
    {
        AllowsCustomFields = true,
        DefaultColumns = ["leadNumber", "subject", "status", "source", "ownerId", "firstReceivedAt"],
        RecordPath = "crm/leadler/{id}",
        ReadyReports =
        [
            new("crm.leads.by-source", "Kaynağa göre lead'ler", "Lead sayısı kaynak bazında.",
                new(Leads, [], [], [new("source")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Bar)),
            new("crm.leads.by-status", "Duruma göre lead'ler", "Lead sayısı durum bazında.",
                new(Leads, [], [], [new("status")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Pie)),
            new("crm.leads.received-by-month", "Aylık gelen lead'ler", "Alınma ayına göre lead sayısı.",
                new(Leads, [], [], [new("firstReceivedAt", DateGrain.Month)], [new(null, Aggregation.Count)], new("firstReceivedAt"), ChartKind.Line)),
        ],
    };
}
