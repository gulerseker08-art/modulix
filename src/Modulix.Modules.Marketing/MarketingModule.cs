using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Marketing.Audiences;
using Modulix.Modules.Marketing.Records;
using Modulix.Modules.Marketing.Responses;
using Modulix.Modules.Marketing.Runs;
using Modulix.Modules.Marketing.Segments;

namespace Modulix.Modules.Marketing;

/// <summary>
/// Pazarlama: segment ve (sonraki dilimlerde) kampanya yönetimi (EPIC 12, ADR-0025). Sektörden ve CRM'den bağımsızdır; müşteri ve kişiler ortak
/// kayıttır, başka modüllerin alanları yalnızca <see cref="Core.Contracts.Marketing.ISegmentFieldSource"/> sözleşmesiyle kullanılır. İzin ve
/// engel kararı çekirdeğin <see cref="Core.Contracts.Communication.ICommunicationPreferences"/> sözleşmesindedir; bu modül tercihin sahibi değildir.
/// </summary>
public sealed class MarketingModule : IModule
{
    public const string ModuleKey = "marketing";

    public static class Permissions
    {
        /// <summary>Segmentleri, sürümleri ve üye kayıtlarını görüntüler; koşul önizlemesinde sayı alır.</summary>
        public const string SegmentsRead = "marketing.segments.read";

        /// <summary>Segment oluşturur, taslağı düzenler, üye ekler/çıkarır, kopyalar.</summary>
        public const string SegmentsWrite = "marketing.segments.write";

        /// <summary>Segmenti yayımlar, yeniler, arşivler ve geri alır.</summary>
        public const string SegmentsPublish = "marketing.segments.publish";

        /// <summary>Hedef listesinde alıcıları (ad, kimlik) görür. Sayı görmek için gerekmez; ortak firma/kişi kaydını okumayı da sağlar.</summary>
        public const string RecipientsRead = "marketing.recipients.read";

        /// <summary>Kampanyaları ve hedef kitle girdilerini görüntürler; hedef kitle önizlemesinde sayı alır.</summary>
        public const string CampaignsRead = "marketing.campaigns.read";

        /// <summary>Kampanya oluşturur, düzenler, hedef kitle girdisi (segment, hedef) ekler/çıkarır.</summary>
        public const string CampaignsWrite = "marketing.campaigns.write";

        /// <summary>Hedef kitleyi dondurur (anlık görüntü) ve kampanyayı arşivler/geri alır. Alıcıları görmek ayrıca <see cref="RecipientsRead"/> ister.</summary>
        public const string CampaignsAudience = "marketing.campaigns.audience";

        /// <summary>Onaya gönderilen çalıştırmayı onaylar ya da reddeder; hazırlayan kendi çalıştırmasını onaylayamaz.</summary>
        public const string CampaignsApprove = "marketing.campaigns.approve";

        /// <summary>Onaylı çalıştırmayı başlatır, işler, duraklatır, sürdürür ve iptal eder.</summary>
        public const string CampaignsSend = "marketing.campaigns.send";

        /// <summary>Kampanya yanıtlarını ve kaynak/UTM bilgisini görüntüler. Kapsam yanıtın sorumlusuna göredir.</summary>
        public const string ResponsesRead = "marketing.responses.read";

        /// <summary>Yanıt kaydeder; yanıtı üstlenir, kapatır, yanıttan lead ve aktivite açar, "iletişim istemiyorum" yanıtının çıkışını uygular. Lead ve aktivite için ayrıca ilgili CRM izni gerekir.</summary>
        public const string ResponsesWrite = "marketing.responses.write";
    }

    public static class SettingKeys
    {
        public const string Provider = "marketing.provider";
        public const string DailyLimit = "marketing.frequency.daily-limit";
        public const string ApprovalRequired = "marketing.approval.required";
    }

    public static class Entities
    {
        public const string Segment = "marketing.segment";
        public const string SegmentMember = "marketing.segment-member";
        public const string SegmentVersion = "marketing.segment-version";
        public const string Campaign = "marketing.campaign";
        public const string CampaignAudience = "marketing.campaign-audience";
        public const string AudienceSnapshot = "marketing.audience-snapshot";
        public const string CampaignContent = "marketing.campaign-content";
        public const string CampaignRun = "marketing.campaign-run";
        public const string CampaignResponse = "marketing.campaign-response";
    }

    /// <summary>Başka modüllerin kayıt anahtarları (yalnızca kayıt boru hattıyla okunur; modül başvurusu yok).</summary>
    public static class ExternalEntities
    {
        public const string Lead = "crm.lead";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Pazarlama";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.SegmentsRead, "Segmentleri ve sürümlerini görüntüler; koşul önizlemesinde sayı alır", "Segmentler"),
        new(Permissions.SegmentsWrite, "Segment oluşturur, taslak koşulu ve üyeleri düzenler", "Segmentler"),
        new(Permissions.SegmentsPublish, "Segmenti yayımlar, yeniler, arşivler ve geri alır", "Segmentler"),
        new(Permissions.RecipientsRead, "Segment üyelerini (firma/kişi adlarını) görüntüler", "Alıcılar"),
        new(Permissions.CampaignsRead, "Kampanyaları ve hedef kitle girdilerini görüntüler; hedef kitle sayılarını alır", "Kampanyalar"),
        new(Permissions.CampaignsWrite, "Kampanya oluşturur, düzenler; hedef kitle girdisi ekler ve çıkarır", "Kampanyalar"),
        new(Permissions.CampaignsAudience, "Hedef kitleyi dondurur; kampanyayı arşivler ve geri alır", "Kampanyalar"),
        new(Permissions.CampaignsApprove, "Çalıştırmayı onaylar ya da reddeder (hazırlayan kendi çalıştırmasını onaylayamaz)", "Kampanyalar"),
        new(Permissions.CampaignsSend, "Onaylı çalıştırmayı başlatır, işler, duraklatır, sürdürür ve iptal eder", "Kampanyalar"),
        new(Permissions.ResponsesRead, "Kampanya yanıtlarını görüntüler", "Yanıtlar"),
        new(Permissions.ResponsesWrite, "Yanıt kaydeder, üstlenir, kapatır; yanıttan lead ve aktivite açar; çıkış yanıtını uygular", "Yanıtlar"),
    ];

    /// <summary>Devre dışı bırakılan kişinin segmentleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Segment, "segment") { Path = "pazarlama/segmentler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "marketing.specialist",
            "Pazarlama uzmanı",
            "Segmentleri görüntüler, oluşturur ve düzenler; alıcıları görür. Yayımlayamaz. Kendi ve ekibinin segmentlerini görür.",
            [Permissions.SegmentsRead, Permissions.SegmentsWrite, Permissions.RecipientsRead, Permissions.CampaignsRead, Permissions.CampaignsWrite, Permissions.CampaignsAudience, Permissions.ResponsesRead, Permissions.ResponsesWrite])
        {
            Scope = RecordScope.Team,
        },
        new(
            "marketing.manager",
            "Pazarlama yöneticisi",
            "Bütün segmentleri yönetir, yayımlar ve arşivler.",
            [Permissions.SegmentsRead, Permissions.SegmentsWrite, Permissions.SegmentsPublish, Permissions.RecipientsRead, Permissions.CampaignsRead, Permissions.CampaignsWrite, Permissions.CampaignsAudience, Permissions.CampaignsApprove, Permissions.CampaignsSend, Permissions.ResponsesRead, Permissions.ResponsesWrite])
        {
            Scope = RecordScope.All,
        },
        new(
            "marketing.viewer",
            "Pazarlama izleyici",
            "Segmentleri ve sayılarını görüntüler; alıcı listesini görmez.",
            [Permissions.SegmentsRead, Permissions.CampaignsRead, Permissions.ResponsesRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Segment, "Segment", Permissions.SegmentsRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama", ["target"] = "Hedef türü", ["kind"] = "Tür", ["purpose"] = "Amaç",
            ["ownerId"] = "Sorumlu", ["status"] = "Durum", ["draft"] = "Taslak koşul", ["publishedVersion"] = "Yayın sürümü",
            ["lastComputedAt"] = "Son hesaplama", ["lastMemberCount"] = "Son üye sayısı", ["lastComputeMs"] = "Hesaplama süresi (ms)", ["lastError"] = "Son hata",
        }, "name")
        {
            UserFields = ["ownerId", "createdBy", "updatedBy"],
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["target"] = new Dictionary<string, string> { ["Company"] = "Firma", ["Person"] = "Kişi", ["Lead"] = "Lead" },
                ["kind"] = new Dictionary<string, string> { ["Static"] = "Statik", ["Dynamic"] = "Dinamik" },
                ["status"] = new Dictionary<string, string> { ["Draft"] = "Taslak", ["Active"] = "Etkin", ["Archived"] = "Arşiv" },
            },
        },
        new(Entities.SegmentMember, "Segment üyesi", Permissions.SegmentsRead, new Dictionary<string, string>
        {
            ["segmentId"] = "Segment", ["targetId"] = "Hedef", ["kind"] = "Tür", ["reason"] = "Gerekçe",
        })
        {
            UserFields = ["addedBy"],
            References = new Dictionary<string, string> { ["segmentId"] = Entities.Segment },
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["kind"] = new Dictionary<string, string> { ["Include"] = "Dahil", ["Exclude"] = "Hariç" },
            },
        },
        new(Entities.SegmentVersion, "Segment sürümü", Permissions.SegmentsRead, new Dictionary<string, string>
        {
            ["segmentId"] = "Segment", ["versionNo"] = "Sürüm", ["definition"] = "Koşul", ["publishedAt"] = "Yayın zamanı",
        })
        {
            UserFields = ["publishedBy"],
            References = new Dictionary<string, string> { ["segmentId"] = Entities.Segment },
        },
        new(Entities.Campaign, "Kampanya", Permissions.CampaignsRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama", ["purpose"] = "Amaç", ["target"] = "Hedef türü", ["channel"] = "Kanal",
            ["recipientRule"] = "Kişi seçme kuralı", ["recipientRole"] = "Aranan rol", ["maxPerTarget"] = "Hedef başına alıcı sınırı",
            ["startsOn"] = "Başlangıç", ["endsOn"] = "Bitiş", ["utmSource"] = "UTM kaynak", ["utmMedium"] = "UTM ortam", ["utmCampaign"] = "UTM kampanya", ["ownerId"] = "Sorumlu", ["status"] = "Durum",
        }, "name")
        {
            UserFields = ["ownerId", "createdBy", "updatedBy"],
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["target"] = new Dictionary<string, string> { ["Company"] = "Firma", ["Person"] = "Kişi", ["Lead"] = "Lead" },
                ["channel"] = new Dictionary<string, string> { ["Email"] = "E-posta", ["Phone"] = "Telefon", ["Sms"] = "SMS", ["WhatsApp"] = "WhatsApp" },
                ["recipientRule"] = new Dictionary<string, string>
                {
                    ["PrimaryContact"] = "Birincil irtibat", ["DecisionMaker"] = "Karar verici", ["Role"] = "Belirli rol", ["AllContacts"] = "Tüm kişiler", ["CompanyPoints"] = "Firma iletişim noktaları",
                },
                ["status"] = new Dictionary<string, string> { ["Draft"] = "Taslak", ["Archived"] = "Arşiv" },
            },
        },
        new(Entities.CampaignAudience, "Kampanya hedef kitle girdisi", Permissions.CampaignsRead, new Dictionary<string, string>
        {
            ["campaignId"] = "Kampanya", ["mode"] = "Tür", ["segmentId"] = "Segment", ["targetId"] = "Hedef", ["reason"] = "Gerekçe",
        })
        {
            UserFields = ["addedBy"],
            References = new Dictionary<string, string> { ["campaignId"] = Entities.Campaign, ["segmentId"] = Entities.Segment },
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mode"] = new Dictionary<string, string> { ["Include"] = "Dahil", ["Exclude"] = "Hariç" },
            },
        },
        new(Entities.AudienceSnapshot, "Dondurulmuş hedef kitle", Permissions.CampaignsRead, new Dictionary<string, string>
        {
            ["campaignId"] = "Kampanya", ["target"] = "Hedef türü", ["channel"] = "Kanal", ["takenAt"] = "Dondurma zamanı", ["targetCount"] = "Hedef sayısı",
            ["resolvedTargetCount"] = "Alıcısı bulunan hedef", ["personCount"] = "Çözümlenen kişi", ["uniqueAddressCount"] = "Benzersiz iletişim noktası",
            ["eligibleCount"] = "Uygun alıcı", ["excludedCount"] = "Elenen alıcı", ["exclusions"] = "Elenme nedenleri", ["unresolved"] = "Alıcısı bulunamayan hedefler",
            ["sources"] = "Kaynaklar",
        })
        {
            UserFields = ["takenBy"],
            References = new Dictionary<string, string> { ["campaignId"] = Entities.Campaign },
        },
        new(Entities.CampaignContent, "Kampanya içeriği", Permissions.CampaignsRead, new Dictionary<string, string>
        {
            ["campaignId"] = "Kampanya", ["versionNo"] = "Sürüm", ["subject"] = "Konu", ["body"] = "Metin",
        })
        {
            UserFields = ["createdBy"],
            References = new Dictionary<string, string> { ["campaignId"] = Entities.Campaign },
        },
        new(Entities.CampaignRun, "Kampanya çalıştırması", Permissions.CampaignsRead, new Dictionary<string, string>
        {
            ["campaignId"] = "Kampanya", ["snapshotId"] = "Hedef kitle", ["contentId"] = "İçerik", ["scheduledAt"] = "Planlanan zaman", ["status"] = "Durum",
            ["note"] = "Gerekçe", ["ownerId"] = "Sorumlu", ["submittedBy"] = "Onaya gönderen", ["submittedAt"] = "Onaya gönderme zamanı", ["approvedBy"] = "Onaylayan",
            ["approvedAt"] = "Onay zamanı", ["approvedSnapshotId"] = "Onaylanan hedef kitle", ["approvedContentId"] = "Onaylanan içerik", ["startedAt"] = "Başlangıç",
            ["completedAt"] = "Bitiş",
        })
        {
            UserFields = ["ownerId", "submittedBy", "approvedBy", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["campaignId"] = Entities.Campaign, ["snapshotId"] = Entities.AudienceSnapshot, ["contentId"] = Entities.CampaignContent },
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string>
                {
                    ["Draft"] = "Taslak", ["PendingApproval"] = "Onay bekliyor", ["Approved"] = "Onaylandı", ["Running"] = "Çalışıyor", ["Paused"] = "Duraklatıldı",
                    ["Completed"] = "Tamamlandı", ["Cancelled"] = "İptal edildi",
                },
            },
        },
        new(Entities.CampaignResponse, "Kampanya yanıtı", Permissions.ResponsesRead, new Dictionary<string, string>
        {
            ["campaignId"] = "Kampanya", ["runId"] = "Çalıştırma", ["deliveryId"] = "İleti", ["targetId"] = "Hedef", ["personId"] = "Kişi", ["kind"] = "Tür", ["note"] = "Not",
            ["receivedAt"] = "Alındığı zaman", ["status"] = "Durum", ["ownerId"] = "Sorumlu", ["leadId"] = "Lead", ["activityId"] = "Aktivite",
            ["optOutAppliedAt"] = "Çıkış uygulama zamanı", ["closedReason"] = "Kapanış gerekçesi",
        })
        {
            UserFields = ["ownerId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["campaignId"] = Entities.Campaign },
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["kind"] = new Dictionary<string, string>
                {
                    ["Interested"] = "İlgilendi", ["Question"] = "Soru", ["MeetingRequest"] = "Görüşme talebi", ["NotInterested"] = "İlgilenmiyor", ["OptOut"] = "İletişim istemiyor",
                    ["Complaint"] = "Şikâyet", ["Other"] = "Diğer",
                },
                ["status"] = new Dictionary<string, string> { ["New"] = "Yeni", ["InProgress"] = "İşlemde", ["Converted"] = "Lead'e dönüştü", ["Closed"] = "Kapalı" },
            },
        },
    ];

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.Provider, "Pazarlama: gönderim sağlayıcısı", "Pazarlama", SettingType.Choice, MarketingPolicy.NoProvider)
        {
            Description = "Varsayılan: sağlayıcı yok, gerçek gönderim bloklu. 'Test sağlayıcısı' iletiyi hiçbir yere göndermez; yalnızca deneme ve geliştirme içindir. Gerçek e-posta/SMS sağlayıcıları bu sürümde yoktur.",
            Options = [new SettingOption(MarketingPolicy.NoProvider, "Yok (gönderim bloklu)"), new SettingOption(MarketingPolicy.FakeProvider, "Test sağlayıcısı (ileti göndermez)")],
        },
        new(SettingKeys.DailyLimit, "Pazarlama: adres başına günlük ileti sınırı", "Pazarlama", SettingType.Text, "1")
        {
            Description = "Bir iletişim noktasına bir günde (firma saat diliminde) gidebilecek pazarlama iletisi sayısı; kampanyalar arasında ortak sayılır. 0 sınırı kapatır.",
            Pattern = @"^[0-9]{1,3}$",
            PatternMessage = "0-999 arası tam sayı girin.",
            MaxLength = 3,
        },
        new(SettingKeys.ApprovalRequired, "Pazarlama: çalıştırma onayı zorunlu", "Pazarlama", SettingType.Choice, "true")
        {
            Description = "Evet seçilirse onaya gönderilen çalıştırmayı hazırlayandan başka, onay yetkisi olan biri onaylar (görev ayrılığı). Hayır seçilirse gönderim doğrudan onaylı sayılır.",
            Options = [new SettingOption("true", "Evet"), new SettingOption("false", "Hayır")],
        },
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Segment, "Segment"),
    ];

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): segment üyesi eklemek ve üye adlarını görmek için firma/kişi okunur; izin alıcı görüntüleme iznidir
    /// ("sayı görmek" için gerekmez). Pazarlama modülü ortak kayıt oluşturmaz.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.RecipientsRead),
        new(SharedEntities.Person, Permissions.RecipientsRead),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<MarketingStoreContext>();
        services.AddScoped<SegmentWorkflowGate>();
        services.AddScoped<IRecordStore, SegmentRecordStore>();
        services.AddScoped<IRecordStore, SegmentMemberRecordStore>();
        services.AddScoped<IRecordStore, SegmentVersionRecordStore>();
        services.AddScoped<ISegmentEngine, SegmentEngine>();
        services.AddScoped<ISegmentService, SegmentService>();
        services.AddScoped<SnapshotPayload>();
        services.AddScoped<SnapshotRecipients>();
        services.AddScoped<IRecordStore, CampaignRecordStore>();
        services.AddScoped<IRecordStore, CampaignAudienceRecordStore>();
        services.AddScoped<IRecordStore, AudienceSnapshotRecordStore>();
        services.AddScoped<ICampaignAudience, CampaignAudienceService>();
        services.AddScoped<IRecordStore, CampaignContentRecordStore>();
        services.AddScoped<IRecordStore, CampaignRunRecordStore>();
        services.AddScoped<DeliveryJournal>();
        services.AddScoped<ICampaignRuns, CampaignRuns>();
        services.AddScoped<IRecordStore, CampaignResponseRecordStore>();
        services.AddScoped<ICampaignResponses, CampaignResponses>();
        services.AddScoped<IMessageProvider, FakeMessageProvider>();
    }
}
