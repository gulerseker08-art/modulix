using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support;

/// <summary>
/// Destek ve şikâyet yönetimi (EPIC 10, ADR-0024): talep kaydı, kategori, öncelik, kuyruk ve atama, durum makinesi, çözüm, mesaj ve iç not.
/// Sektörden bağımsızdır; müşteri ve kişiler ortak kayıttır (ADR-0006), CRM tablolarına bağlanmaz, bu yüzden CRM satın alınmadan da
/// kullanılır. Diğer modüllerin kayıtlarına yalnızca isteğe bağlı, güvenli referanslarla bağlanır.
/// </summary>
public sealed class SupportModule : IModule
{
    public const string ModuleKey = "support";

    public static class Permissions
    {
        public const string TicketsRead = "support.tickets.read";

        /// <summary>Yeni talep açar (kayıt, kanal girişi, hızlı kayıt). Düzenlemeden ayrıdır.</summary>
        public const string TicketsCreate = "support.tickets.create";

        /// <summary>Talebin başvuran, kategori, etki/aciliyet gibi alanlarını düzenler; sınıflandırır, işleme alır, bekletir, sürdürür.</summary>
        public const string TicketsWrite = "support.tickets.write";

        /// <summary>Talebi kullanıcıya/kuyruğa atar ve ekipler arası devreder. Üzerine almaktan ayrıdır.</summary>
        public const string TicketsAssign = "support.tickets.assign";

        /// <summary>Atanmamış talebi kendi üzerine alır.</summary>
        public const string TicketsTake = "support.tickets.take";

        /// <summary>Çözüm kaydeder (çözüm kodu ve açıklaması zorunlu).</summary>
        public const string TicketsResolve = "support.tickets.resolve";

        /// <summary>Çözülmüş talebi kapatır.</summary>
        public const string TicketsClose = "support.tickets.close";

        /// <summary>Çözülmüş ya da (pencere içinde) kapanmış talebi gerekçeyle yeniden açar.</summary>
        public const string TicketsReopen = "support.tickets.reopen";

        /// <summary>Talebi gerekçeyle iptal eder. Silme yoktur.</summary>
        public const string TicketsCancel = "support.tickets.cancel";

        /// <summary>Hesaplanan önceliği gerekçeyle elle değiştirir.</summary>
        public const string PriorityOverride = "support.tickets.priority";

        /// <summary>Müşteriye görünen yanıt yazar ve müşteriden gelen mesajı (telefon, e-posta) kaydeder.</summary>
        public const string MessagesReply = "support.messages.reply";

        /// <summary>İç not yazar. Müşteriye hiçbir yoldan gitmez.</summary>
        public const string NotesWrite = "support.notes.write";

        /// <summary>İç notları okur. Olmayan kişi talebi ve müşteriye görünen mesajları görür, iç notları görmez.</summary>
        public const string InternalRead = "support.internal.read";

        /// <summary>Kuyrukları ve kategorileri yönetir.</summary>
        public const string SettingsManage = "support.settings.manage";

        /// <summary>Mükerrer talebi başka bir talebe birleştirir (geri alınamaz).</summary>
        public const string TicketsMerge = "support.tickets.merge";

        /// <summary>Görebildiği talepleri CSV olarak dışa aktarır (iç not ve mesaj gövdesi içermez).</summary>
        public const string TicketsExport = "support.tickets.export";

        /// <summary>Destek raporlarını (hacim, birikim, süre, SLA uyumu, yeniden açılma, memnuniyet, şikâyet inceleme) okur. Veriler yine çağıranın veri kapsamıyla sınırlıdır.</summary>
        public const string ReportsRead = "support.reports.read";

        /// <summary>Şikâyetlerin düzeltici aksiyonlarını açar, tamamlar, iptal eder ve yeniden açar.</summary>
        public const string ComplaintsManage = "support.complaints.manage";

        /// <summary>Gelen kanal adaptörü olarak mesaj girer (talep açar, talebe mesaj ekler). Entegrasyon kullanıcısına verilir; oluşturma ve yanıt izinleri de gerekir.</summary>
        public const string InboundIngest = "support.inbound.ingest";

        /// <summary>SLA politikalarını ve iş takvimlerini yönetir; örnek talep üzerinde politika önizlemesi yapar.</summary>
        public const string SlaManage = "support.sla.manage";

        /// <summary>Talebin açık SLA örneklerini gerekçeyle güncel politika ve takvimle yeniden hesaplar.</summary>
        public const string SlaRecalculate = "support.sla.recalculate";
    }

    /// <summary>Firma ayarları.</summary>
    public static class SettingKeys
    {
        public const string ReopenWindowDays = "support.reopen.window-days";
        public const string RequireRootCause = "support.complaint.require-root-cause";
        public const string AutoCloseDays = "support.auto-close-days";
        public const string SurveyEnabled = "support.survey.enabled";
        public const string SurveyValidDays = "support.survey.valid-days";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Ticket = "support.ticket";
        public const string Queue = "support.queue";
        public const string Category = "support.category";
        public const string SlaCalendar = "support.sla-calendar";
        public const string SlaPolicy = "support.sla-policy";
        public const string PriorityMatrix = "support.priority-matrix";
        public const string RoutingRule = "support.routing-rule";
    }

    /// <summary>Seçim listeleri (Y9b): firma adlarını, sırasını ve etkinliğini değiştirir.</summary>
    public static class Lists
    {
        public const string TicketType = "support.ticket-type";
        public const string ResolutionCode = "support.resolution-code";
    }

    /// <summary>Talep türü kategorileri: kodun anladığı davranış (ör. şikâyette kök neden zorunluluğu).</summary>
    public static class TypeCategories
    {
        public const string Support = "support";
        public const string Complaint = "complaint";
        public const string Info = "info";
        public const string Service = "service";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Destek";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.TicketsRead, "Destek taleplerini, tarihçesini ve müşteriye görünen mesajlarını görüntüler", "Destek talepleri"),
        new(Permissions.TicketsCreate, "Yeni destek talebi, şikâyet, bilgi talebi ya da hizmet isteği açar", "Destek talepleri"),
        new(Permissions.TicketsWrite, "Talebin alanlarını düzenler; sınıflandırır, işleme alır, bekletir ve sürdürür", "Destek talepleri"),
        new(Permissions.TicketsAssign, "Talebi kullanıcıya ya da kuyruğa atar; ekipler arası devreder", "Destek talepleri"),
        new(Permissions.TicketsTake, "Atanmamış talebi kendi üzerine alır", "Destek talepleri"),
        new(Permissions.TicketsResolve, "Talebin çözümünü kaydeder", "Destek talepleri"),
        new(Permissions.TicketsClose, "Çözülmüş talebi kapatır", "Destek talepleri"),
        new(Permissions.TicketsReopen, "Çözülmüş ya da pencere içinde kapanmış talebi gerekçeyle yeniden açar", "Destek talepleri"),
        new(Permissions.TicketsCancel, "Talebi gerekçeyle iptal eder", "Destek talepleri"),
        new(Permissions.PriorityOverride, "Hesaplanan önceliği gerekçeyle elle değiştirir", "Destek talepleri"),
        new(Permissions.MessagesReply, "Müşteriye görünen yanıt yazar; müşteriden gelen mesajı kaydeder", "Destek mesajları"),
        new(Permissions.NotesWrite, "İç not yazar (müşteriye gitmez)", "Destek mesajları"),
        new(Permissions.InternalRead, "İç notları okur", "Destek mesajları"),
        new(Permissions.SettingsManage, "Destek kuyruklarını ve kategorilerini yönetir", "Destek yönetimi"),
        new(Permissions.TicketsMerge, "Mükerrer talebi başka bir talebe birleştirir (geri alınamaz)", "Destek talepleri"),
        new(Permissions.TicketsExport, "Görebildiği talepleri CSV olarak dışa aktarır", "Destek talepleri"),
        new(Permissions.InboundIngest, "Gelen kanal adaptörü olarak mesaj girer (talep açar, talebe mesaj ekler)", "Destek yönetimi"),
        new(Permissions.ReportsRead, "Destek raporlarını ve şikâyet incelemesini görüntüler (veri kapsamıyla sınırlı)", "Destek raporları"),
        new(Permissions.ComplaintsManage, "Şikâyetlerin düzeltici aksiyonlarını açar, tamamlar, iptal eder", "Destek talepleri"),
        new(Permissions.SlaManage, "SLA politikalarını ve iş takvimlerini yönetir; politika önizlemesi yapar", "Destek yönetimi"),
        new(Permissions.SlaRecalculate, "Talebin açık SLA örneklerini gerekçeyle yeniden hesaplar", "Destek talepleri"),
    ];

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.ReopenWindowDays, "Destek: yeniden açma penceresi (gün)", "Destek", SettingType.Text, "7")
        {
            Description = "Kapanan talep bu süre içinde yetkili kişice gerekçeyle yeniden açılabilir. Süre dolunca yeni, ilişkili bir talep açılır. 1-90 arası.",
            Pattern = @"^[0-9]{1,2}$",
            PatternMessage = "1-90 arası tam sayı girin.",
            MaxLength = 2,
        },
        new(SettingKeys.AutoCloseDays, "Destek: çözülen talebi otomatik kapatma süresi (gün)", "Destek", SettingType.Text, "0")
        {
            Description = "Çözüldü durumunda müşteri itirazı gelmeden bu kadar gün geçerse talep otomatik kapanır (zamanlanmış iş). 0 kapalıdır (varsayılan). Müşteri mesajı talebi yeniden açar; müşteri teyidi hemen kapatır.",
            Pattern = @"^[0-9]{1,3}$",
            PatternMessage = "0-999 arası tam sayı girin.",
            MaxLength = 3,
        },
        new(SettingKeys.SurveyEnabled, "Destek: çözümde memnuniyet anketi gönder", "Destek", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse talep çözüldüğünde, başvuranın e-postası varsa süreli ve tek kullanımlık anket bağlantısı e-posta kuyruğuna yazılır. Varsayılan hayırdır.",
            Options = [new SettingOption("true", "Evet"), new SettingOption("false", "Hayır")],
        },
        new(SettingKeys.SurveyValidDays, "Destek: anket bağlantısının geçerlilik süresi (gün)", "Destek", SettingType.Text, "14")
        {
            Description = "Anket bağlantısı bu süre sonunda geçersiz olur. 1-90 arası.",
            Pattern = @"^[0-9]{1,2}$",
            PatternMessage = "1-90 arası tam sayı girin.",
            MaxLength = 2,
        },
        new(SettingKeys.RequireRootCause, "Destek: şikâyet çözümünde kök neden zorunlu", "Destek", SettingType.Choice, "true")
        {
            Description = "Evet seçilirse şikâyet türündeki talep, kök neden ve düzeltici aksiyon yazılmadan çözülemez.",
            Options = [new SettingOption("true", "Evet"), new SettingOption("false", "Hayır")],
        },
    ];

    /// <summary>Devre dışı bırakılan kişinin talepleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Ticket, "talep") { Path = "destek/talepler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "support.agent",
            "Destek temsilcisi",
            "Talepleri görüntüler, açar, üzerine alır, yanıtlar, iç not yazar ve çözer. Kendi ve ekibinin taleplerini ve atanmamış talepleri görür.",
            [
                Permissions.TicketsRead, Permissions.TicketsCreate, Permissions.TicketsWrite, Permissions.TicketsTake, Permissions.TicketsResolve,
                Permissions.MessagesReply, Permissions.NotesWrite, Permissions.InternalRead,
            ])
        {
            Scope = RecordScope.Team,
        },
        new(
            "support.lead",
            "Destek ekip lideri",
            "Temsilcinin yetkilerine ek olarak atar, devreder, kapatır, yeniden açar, iptal eder ve önceliği gerekçeyle değiştirir.",
            [
                Permissions.TicketsRead, Permissions.TicketsCreate, Permissions.TicketsWrite, Permissions.TicketsAssign, Permissions.TicketsTake,
                Permissions.TicketsResolve, Permissions.TicketsClose, Permissions.TicketsReopen, Permissions.TicketsCancel, Permissions.PriorityOverride,
                Permissions.MessagesReply, Permissions.NotesWrite, Permissions.InternalRead, Permissions.SlaRecalculate, Permissions.TicketsMerge, Permissions.TicketsExport, Permissions.ReportsRead, Permissions.ComplaintsManage,
            ])
        {
            Scope = RecordScope.Team,
        },
        new(
            "support.manager",
            "Destek yöneticisi",
            "Bütün talepleri yönetir; kuyruk ve kategori ayarlarını yapar.",
            [
                Permissions.TicketsRead, Permissions.TicketsCreate, Permissions.TicketsWrite, Permissions.TicketsAssign, Permissions.TicketsTake,
                Permissions.TicketsResolve, Permissions.TicketsClose, Permissions.TicketsReopen, Permissions.TicketsCancel, Permissions.PriorityOverride,
                Permissions.MessagesReply, Permissions.NotesWrite, Permissions.InternalRead, Permissions.SettingsManage, Permissions.SlaManage, Permissions.SlaRecalculate, Permissions.TicketsMerge, Permissions.TicketsExport, Permissions.ReportsRead, Permissions.ComplaintsManage,
            ])
        {
            Scope = RecordScope.All,
        },
        new(
            "support.integration",
            "Destek kanal entegrasyonu",
            "Gelen kanal adaptörü (e-posta, web formu) için servis kullanıcısı: talep açar ve talebe mesaj ekler; başka bir şey yapamaz. Kapsam tümü olmalıdır.",
            [Permissions.TicketsRead, Permissions.TicketsCreate, Permissions.MessagesReply, Permissions.InboundIngest])
        {
            Scope = RecordScope.All,
        },
        new(
            "support.viewer",
            "Destek izleyici",
            "Talepleri ve müşteriye görünen mesajları görüntüler; iç notları görmez.",
            [Permissions.TicketsRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Ticket, "Destek talebi", Permissions.TicketsRead, new Dictionary<string, string>
        {
            ["number"] = "Numara", ["subject"] = "Konu", ["description"] = "Açıklama", ["typeKey"] = "Tür", ["channel"] = "Kanal", ["partyId"] = "Müşteri",
            ["personId"] = "İlgili kişi", ["reporterName"] = "Başvuran", ["reporterEmail"] = "Başvuran e-postası", ["reporterPhone"] = "Başvuran telefonu",
            ["categoryId"] = "Kategori", ["impact"] = "Etki", ["urgency"] = "Aciliyet", ["priority"] = "Öncelik", ["priorityOverridden"] = "Öncelik elle değiştirildi",
            ["priorityReason"] = "Öncelik gerekçesi", ["status"] = "Durum", ["statusReason"] = "Durum gerekçesi", ["queueId"] = "Kuyruk", ["ownerId"] = "Sorumlu",
            ["parentTicketId"] = "İlişkili talep", ["eventDate"] = "Olay tarihi", ["expectedOutcome"] = "Beklenen sonuç", ["resolutionCode"] = "Çözüm kodu",
            ["resolutionNote"] = "Çözüm açıklaması", ["resolvedAt"] = "Çözüm zamanı", ["rootCause"] = "Kök neden", ["correctiveAction"] = "Düzeltici aksiyon",
            ["mergedIntoId"] = "Birleştirildiği talep", ["closedAt"] = "Kapanış zamanı", ["reopenCount"] = "Yeniden açılma sayısı", ["firstResponseAt"] = "İlk yanıt zamanı",
            ["firstResponseDueAt"] = "İlk yanıt hedefi", ["resolutionDueAt"] = "Çözüm hedefi",
        }, "subject")
        {
            UserFields = ["ownerId", "resolvedBy", "createdBy", "updatedBy"],
            References = new Dictionary<string, string>
            {
                ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person, ["categoryId"] = Entities.Category,
                ["queueId"] = Entities.Queue, ["parentTicketId"] = Entities.Ticket,
            },
        },
        new(Entities.Queue, "Destek kuyruğu", Permissions.SettingsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["isDefault"] = "Varsayılan", ["isActive"] = "Etkin",
        }, "name")
        {
            UserFields = ["createdBy", "updatedBy"],
        },
        new(Entities.Category, "Talep kategorisi", Permissions.SettingsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["parentId"] = "Üst kategori", ["defaultQueueId"] = "Varsayılan kuyruk", ["isActive"] = "Etkin", ["position"] = "Sıra", ["requiredFields"] = "Zorunlu alanlar",
        }, "name")
        {
            UserFields = ["createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["parentId"] = Entities.Category, ["defaultQueueId"] = Entities.Queue },
        },
        new(Entities.PriorityMatrix, "Öncelik matrisi hücresi", Permissions.SettingsManage, new Dictionary<string, string>
        {
            ["impact"] = "Etki", ["urgency"] = "Aciliyet", ["priority"] = "Öncelik",
        })
        {
            UserFields = ["createdBy", "updatedBy"],
        },
        new(Entities.RoutingRule, "Yönlendirme kuralı", Permissions.SettingsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["order"] = "Sıra", ["isActive"] = "Etkin", ["channel"] = "Kanal", ["typeKey"] = "Tür", ["categoryId"] = "Kategori", ["priority"] = "Öncelik",
            ["partyId"] = "Müşteri", ["queueId"] = "Kuyruk", ["ownerId"] = "Sorumlu",
        }, "name")
        {
            UserFields = ["ownerId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["categoryId"] = Entities.Category, ["partyId"] = SharedEntities.Party, ["queueId"] = Entities.Queue },
        },
        new(Entities.SlaCalendar, "SLA takvimi", Permissions.SlaManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["timeZoneId"] = "Saat dilimi", ["isAlwaysOpen"] = "7/24", ["weeklyHours"] = "Çalışma saatleri", ["holidays"] = "Tatiller", ["isActive"] = "Etkin",
        }, "name")
        {
            UserFields = ["createdBy", "updatedBy"],
        },
        new(Entities.SlaPolicy, "SLA politikası", Permissions.SlaManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["description"] = "Açıklama", ["isActive"] = "Etkin", ["isDefault"] = "Varsayılan", ["precedence"] = "Öncelik sırası", ["typeKey"] = "Tür",
            ["categoryId"] = "Kategori", ["priority"] = "Öncelik", ["partyId"] = "Müşteri", ["firstResponseMinutes"] = "İlk yanıt hedefi (dk)", ["resolutionMinutes"] = "Çözüm hedefi (dk)",
            ["contractId"] = "Sözleşme", ["calendarId"] = "Takvim", ["pauseOnWaitingCustomer"] = "Müşteri beklemesinde durdur", ["pauseOnWaitingInternal"] = "İç ekip beklemesinde durdur",
            ["escalationRules"] = "Eskalasyon kuralları", ["revision"] = "Sürüm",
        }, "name")
        {
            UserFields = ["createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["categoryId"] = Entities.Category, ["partyId"] = SharedEntities.Party, ["calendarId"] = Entities.SlaCalendar },
        },
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Ticket, "Destek talebi"),
    ];

    /// <summary>
    /// Seçim listeleri. Talep türü yönetilebilir: firma yeni tür ekleyebilir; kategori (support, complaint, info, service) kodun anladığı
    /// davranıştır (ör. şikâyet kategorisindeki türlerde kök neden istenir). Çözüm kodları da firmaya göre düzenlenir.
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.TicketType,
            "Talep türü",
            Entities.Ticket,
            "typeKey",
            Required: true,
            [
                new(TypeCategories.Support, "Destek talebi"), new(TypeCategories.Complaint, "Şikâyet"),
                new(TypeCategories.Info, "Bilgi talebi"), new(TypeCategories.Service, "Hizmet isteği"),
            ],
            [
                new("Support", "Destek talebi", TypeCategories.Support),
                new("Complaint", "Şikâyet", TypeCategories.Complaint),
                new("Info", "Bilgi talebi", TypeCategories.Info),
                new("Service", "Hizmet isteği", TypeCategories.Service),
            ])
        {
            Default = "Support",
        },
        new(
            Lists.ResolutionCode,
            "Çözüm kodu",
            Entities.Ticket,
            "resolutionCode",
            Required: false,
            [],
            [
                new("Resolved", "Çözüldü"), new("Workaround", "Geçici çözüm uygulandı"), new("InfoProvided", "Bilgi verildi"),
                new("NoFaultFound", "Arıza/hata bulunamadı"), new("Duplicate", "Mükerrer talep"), new("CustomerWithdrew", "Müşteri vazgeçti"),
                new("OutOfScope", "Kapsam dışı"),
            ]),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => SupportLookupDefinitions.All;

    /// <summary>Ortak kayıtlar (ADR-0006): müşteri ve kişi, talep okuma izniyle okunur. Destek modülü ortak kayıt oluşturmaz.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.TicketsRead),
        new(SharedEntities.Person, Permissions.TicketsRead),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Ticket, "partyId", "destek talebi"),
        new(SharedEntities.Person, Entities.Ticket, "personId", "destek talebi"),
    ];

    /// <summary>
    /// Sistem kimliği (oturumsuz çağıran): SLA zamanlanmış işi (ihlal işareti, eskalasyon) bu kimlikle, kendi tenant kapsamında ve yalnızca bildirilen izinlerle çalışır.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        // Herkese açık memnuniyet anketi sistem adına çalışır; yalnızca talebi okur ve tarihçe/son hareket yazar.
        EventSubscription.For<SupportSurveyAnchor>(
            "support.survey",
            [DomainEventTypes.RecordDeleted],
            [Entities.Ticket],
            [Permissions.TicketsRead, Permissions.TicketsWrite]),
        EventSubscription.For<SupportSchedulerAnchor>(
            "support.scheduler",
            [DomainEventTypes.RecordDeleted],
            [Entities.Ticket],
            [Permissions.TicketsRead, Permissions.TicketsWrite, Permissions.TicketsAssign, Permissions.TicketsClose]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<SupportStoreContext>();
        services.AddScoped<TicketWorkflowGate>();
        services.AddScoped<SupportReader>();
        services.AddScoped<IRecordStore, SupportTicketRecordStore>();
        services.AddScoped<IRecordStore, SupportQueueRecordStore>();
        services.AddScoped<IRecordStore, TicketCategoryRecordStore>();
        services.AddScoped<ITicketWorkflow, TicketWorkflow>();
        services.AddScoped<ITicketMessages, TicketMessages>();
        services.AddScoped<ITicketQueries, TicketQueries>();
        services.AddScoped<ITicketSummary, TicketSummaryService>();
        services.AddScoped<IRecordStore, SlaCalendarRecordStore>();
        services.AddScoped<IRecordStore, SlaPolicyRecordStore>();
        services.AddScoped<ITicketSla, TicketSla>();
        services.AddScoped<ISlaScheduler, SlaScheduler>();
        services.AddScoped<IRecordStore, PriorityMatrixRecordStore>();
        services.AddScoped<IRecordStore, TicketRoutingRuleRecordStore>();
        services.AddScoped<IPriorityMatrixService, PriorityMatrixService>();
        services.AddScoped<ITicketRoutingPreview, TicketRoutingPreview>();
        services.AddScoped<ITicketDuplicates, TicketDuplicates>();
        services.AddScoped<ITicketMerge, TicketMerge>();
        services.AddScoped<ITicketTasks, TicketTasks>();
        services.AddScoped<ITicketAttachments, TicketAttachments>();
        services.AddScoped<ITicketLinks, TicketLinks>();
        services.AddScoped<ICorrectiveActions, CorrectiveActions>();
        services.AddScoped<ISupportReports, SupportReports>();
        services.AddScoped<ITicketInbound, TicketInbound>();
        services.AddScoped<SurveyIssuer>();
        services.AddScoped<ITicketSurveys, TicketSurveys>();
        services.AddScoped<ISurveyPortal, SurveyPortal>();
        services.AddScoped<ITicketBulk, TicketBulk>();
        services.AddScoped<SupportSurveyAnchor>();
        services.AddScoped<SupportSchedulerAnchor>();
        services.AddScoped<IActivityLinkTarget, TicketLinkTarget>();
    }
}
