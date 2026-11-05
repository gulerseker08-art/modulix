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
    public const string Quotes = "crm.quotes";
    public const string Companies = "crm.companies";
    public const string Targets = "crm.sales-targets";
    public const string Forecasts = "crm.forecasts";

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


    private static readonly IReadOnlyList<DatasetOption> QuoteStatuses =
    [
        new("Draft", "Taslak"), new("Presented", "Sunuldu"), new("Accepted", "Kabul edildi"), new("Rejected", "Reddedildi"),
        new("Expired", "Süresi doldu"), new("Cancelled", "İptal"), new("Superseded", "Yeni revizyonla değişti"),
    ];

    private static readonly IReadOnlyList<DatasetOption> QuoteApprovals =
    [
        new("NotRequired", "Gerekmiyor"), new("Pending", "Bekliyor"), new("Approved", "Onaylandı"), new("Rejected", "Reddedildi"),
        new("ChangesRequested", "Değişiklik istendi"),
    ];

    private static readonly IReadOnlyList<DatasetOption> QuoteDeliveries =
    [
        new("NotSent", "Gönderilmedi"), new("Pending", "Bekliyor"), new("Processing", "İşleniyor"), new("Succeeded", "Teslim edildi"), new("Failed", "Başarısız"),
    ];

    private static readonly IReadOnlyList<DatasetOption> TargetMetrics = [new("WonAmount", "Kazanılan tutar"), new("WonCount", "Kazanılan adet")];

    private static readonly IReadOnlyList<DatasetOption> TargetScopes = [new("Tenant", "Firma geneli"), new("User", "Kullanıcı")];

    private static readonly IReadOnlyList<DatasetOption> ForecastStatuses = [new("Draft", "Taslak"), new("Submitted", "Sunuldu"), new("Locked", "Kilitlendi")];

    private static readonly IReadOnlyList<DatasetOption> PeriodKinds = [new("Month", "Ay"), new("Quarter", "Çeyrek"), new("Year", "Yıl")];

    private const ColumnUse All = ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show;
    private const ColumnUse FilterShow = ColumnUse.Filter | ColumnUse.Show;

    public static ModuleReporting Reporting { get; } = new(
        [OpportunityDataset(), LeadDataset(), QuoteDataset(), CompanyDataset(), TargetDataset(), ForecastDataset()],
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

    // Teklif: her satır bir teklif revizyonudur (aile değil); "yalnız son revizyon" süzgeci yoktur, bu yüzden revizyon sayısı tekliflerin sayısını şişirir.
    // Aile bazlı kabul oranı ve kohortlar Satış analizi ekranındadır. Tutar sütunları teklifin net (vergi hariç) ve brüt toplamıdır.
    private static DatasetDefinition QuoteDataset() => new(
        Quotes, "Teklifler", CrmModule.Entities.Quote, 1,
        [
            new("quoteNumber", "Teklif no", DatasetColumnKind.Text, FilterShow),
            new("title", "Başlık", DatasetColumnKind.Text, FilterShow),
            new("revisionNo", "Revizyon", DatasetColumnKind.Number, ColumnUse.Measure | FilterShow),
            new("status", "Durum", DatasetColumnKind.Choice, All) { Options = QuoteStatuses, StoreFilter = "status" },
            new("approvalStatus", "Onay durumu", DatasetColumnKind.Choice, All) { Options = QuoteApprovals, StoreFilter = "approvalStatus" },
            new("deliveryStatus", "Gönderim durumu", DatasetColumnKind.Choice, All) { Options = QuoteDeliveries, StoreFilter = "deliveryStatus" },
            new("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.QuoteOwner, StoreFilter = "ownerId" },
            new("companyId", "Firma", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.QuoteCompany, StoreFilter = "companyId" },
            new("currency", "Para birimi", DatasetColumnKind.Text, All) { StoreFilter = "currency" },
            new("netTotal", "Net toplam", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "currency" },
            new("grossTotal", "Brüt toplam", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "currency" },
            new("issueDate", "Düzenleme tarihi", DatasetColumnKind.Date, All),
            new("presentedAt", "Sunulma", DatasetColumnKind.DateTime, All),
            new("decidedAt", "Karar", DatasetColumnKind.DateTime, All),
            new("validUntil", "Geçerlilik sonu", DatasetColumnKind.DateTime, All),
        ])
    {
        DefaultColumns = ["quoteNumber", "title", "status", "companyId", "ownerId", "netTotal", "validUntil"],
        RecordPath = "crm/teklifler/{id}",
        ReadyReports =
        [
            new("crm.quotes.by-status", "Duruma göre teklifler", "Teklif revizyonlarının adedi ve net tutarı, durum bazında (para birimine göre ayrı).",
                new(Quotes, [], [], [new("status")], [new(null, Aggregation.Count), new("netTotal", Aggregation.Sum)], new("count", SortDirection.Descending), ChartKind.Bar)),
            new("crm.quotes.accepted-by-month", "Aylık kabul edilen teklifler", "Kabul edilen tekliflerin karar ayına göre adedi ve net tutarı.",
                new(Quotes, [], [new("status", FilterOperator.Equals, "Accepted")], [new("decidedAt", DateGrain.Month)], [new(null, Aggregation.Count), new("netTotal", Aggregation.Sum)],
                    new("decidedAt"), ChartKind.Line)),
            new("crm.quotes.presented-list", "Sunulmuş teklifler", "Sunulmuş (karar bekleyen) tekliflerin geçerlilik sonuna göre listesi.",
                new(Quotes, ["quoteNumber", "title", "companyId", "ownerId", "netTotal", "validUntil"], [new("status", FilterOperator.Equals, "Presented")], [], [], new("validUntil"))),
        ],
    };

    private static DatasetDefinition CompanyDataset() => new(
        Companies, "Firmalar", CrmModule.Entities.Company, 1,
        [
            new("name", "Ad", DatasetColumnKind.Text, FilterShow),
            new("status", "Durum", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.CompanyStatus, StoreFilter = "status" },
            new("source", "Kaynak", DatasetColumnKind.Choice, All) { ChoiceList = CrmModule.Lists.CompanySource, StoreFilter = "source" },
            new("industry", "Sektör", DatasetColumnKind.Text, All),
            new("city", "Şehir", DatasetColumnKind.Text, All),
            new("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = CrmLookupDefinitions.CompanyOwner, StoreFilter = "ownerId" },
            new("tags", "Etiketler", DatasetColumnKind.Text, FilterShow),
        ])
    {
        AllowsCustomFields = true,
        DefaultColumns = ["name", "status", "industry", "city", "ownerId"],
        RecordPath = "crm/firmalar/{id}",
        ReadyReports =
        [
            new("crm.companies.by-industry", "Sektöre göre firmalar", "Firma sayısı sektör bazında.",
                new(Companies, [], [], [new("industry")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Bar)),
            new("crm.companies.by-status", "Duruma göre firmalar", "Firma sayısı durum bazında.",
                new(Companies, [], [], [new("status")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Pie)),
            new("crm.companies.by-owner", "Sorumluya göre firmalar", "Firma sayısı sorumlu bazında.",
                new(Companies, [], [], [new("ownerId")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Bar)),
        ],
    };

    // Satış hedefi: her satır bir hedef dilimidir (metrik × kapsam × para birimi × dönem). Adet hedeflerinde para birimi yoktur; değer sütunu "tutar"
    // olduğundan para birimsiz (adet) hedefler tutar toplamına girmez ve "dışlanan kayıt" olarak sayılır; adet hedefleri için "Değer"i tabloda görün.
    private static DatasetDefinition TargetDataset() => new(
        Targets, "Satış hedefleri", CrmModule.Entities.SalesTarget, 1,
        [
            new("metric", "Metrik", DatasetColumnKind.Choice, All) { Options = TargetMetrics, StoreFilter = "metric" },
            new("scopeType", "Kapsam", DatasetColumnKind.Choice, All) { Options = TargetScopes, StoreFilter = "scopeType" },
            new("scopeUserId", "Kullanıcı", DatasetColumnKind.User, All) { StoreFilter = "scopeUserId" },
            new("currency", "Para birimi", DatasetColumnKind.Text, All),
            new("periodStart", "Dönem başı", DatasetColumnKind.Date, All) { StoreFilterFrom = "from", StoreFilterTo = "to" },
            new("periodEnd", "Dönem sonu", DatasetColumnKind.Date, All) { StoreFilterFrom = "from", StoreFilterTo = "to" },
            new("value", "Hedef değeri", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "currency" },
            new("revisionNo", "Sürüm", DatasetColumnKind.Number, FilterShow),
            new("note", "Not", DatasetColumnKind.Text, FilterShow),
        ])
    {
        DefaultColumns = ["metric", "scopeType", "scopeUserId", "periodStart", "periodEnd", "value", "currency"],
        ReadyReports =
        [
            new("crm.sales-targets.list", "Satış hedefleri listesi", "Tüm hedef dilimleri, dönem başına.",
                new(Targets, ["metric", "scopeType", "scopeUserId", "periodStart", "periodEnd", "value", "currency"], [], [], [], new("periodStart"))),
            new("crm.sales-targets.by-user", "Kullanıcıya göre tutar hedefleri", "Kazanılan tutar hedeflerinin kullanıcı bazında toplamı (para birimine göre ayrı).",
                new(Targets, [], [new("metric", FilterOperator.Equals, "WonAmount"), new("scopeType", FilterOperator.Equals, "User")], [new("scopeUserId")],
                    [new("value", Aggregation.Sum)], new("sum:value", SortDirection.Descending), ChartKind.Bar)),
        ],
    };

    // Tahmin sürümleri: başlık satırı (dönem, kapsam, sürüm, durum, kilit anındaki gerçekleşen). Fırsat satırları ve düzeltmeler Satış tahmini
    // ekranındadır. Kilit anındaki gerçekleşen tutar, fırsat tutarından türediği için tutar iznine bağlıdır (hassas alan).
    private static DatasetDefinition ForecastDataset() => new(
        Forecasts, "Satış tahmini sürümleri", CrmModule.Entities.ForecastSubmission, 1,
        [
            new("periodKind", "Dönem türü", DatasetColumnKind.Choice, All) { Options = PeriodKinds },
            new("periodStart", "Dönem başı", DatasetColumnKind.Date, All) { StoreFilterFrom = "from", StoreFilterTo = "to" },
            new("periodEnd", "Dönem sonu", DatasetColumnKind.Date, All) { StoreFilterFrom = "from", StoreFilterTo = "to" },
            new("scopeType", "Kapsam", DatasetColumnKind.Choice, All) { Options = TargetScopes },
            new("scopeUserId", "Kullanıcı", DatasetColumnKind.User, All) { StoreFilter = "scopeUserId" },
            new("reportingCurrency", "Raporlama para birimi", DatasetColumnKind.Text, All),
            new("versionNo", "Sürüm", DatasetColumnKind.Number, FilterShow),
            new("status", "Durum", DatasetColumnKind.Choice, All) { Options = ForecastStatuses, StoreFilter = "status" },
            new("realizedAtLock", "Kilit anındaki gerçekleşen", DatasetColumnKind.Money, ColumnUse.Measure | FilterShow) { CurrencyField = "reportingCurrency" },
            new("realizedPartial", "Gerçekleşen eksik (kur yok)", DatasetColumnKind.Flag, All),
            new("submittedAt", "Sunulma", DatasetColumnKind.DateTime, All),
            new("lockedAt", "Kilitlenme", DatasetColumnKind.DateTime, All),
            new("note", "Not", DatasetColumnKind.Text, FilterShow),
        ])
    {
        DefaultColumns = ["periodKind", "periodStart", "scopeType", "scopeUserId", "versionNo", "status", "realizedAtLock", "reportingCurrency"],
        ReadyReports =
        [
            new("crm.forecasts.locked", "Kilitli tahmin sürümleri", "Kilitlenmiş tahmin sürümleri ve kilit anındaki gerçekleşen tutar.",
                new(Forecasts, ["periodKind", "periodStart", "scopeType", "scopeUserId", "versionNo", "realizedAtLock", "reportingCurrency", "lockedAt"],
                    [new("status", FilterOperator.Equals, "Locked")], [], [], new("periodStart", SortDirection.Descending))),
            new("crm.forecasts.by-status", "Duruma göre tahmin sürümleri", "Tahmin sürümlerinin durum dağılımı.",
                new(Forecasts, [], [], [new("status")], [new(null, Aggregation.Count)], new("count", SortDirection.Descending), ChartKind.Pie)),
        ],
    };
}
