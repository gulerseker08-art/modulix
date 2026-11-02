using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Marketing.Records;
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
    }

    public static class Entities
    {
        public const string Segment = "marketing.segment";
        public const string SegmentMember = "marketing.segment-member";
        public const string SegmentVersion = "marketing.segment-version";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Pazarlama";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.SegmentsRead, "Segmentleri ve sürümlerini görüntüler; koşul önizlemesinde sayı alır", "Segmentler"),
        new(Permissions.SegmentsWrite, "Segment oluşturur, taslak koşulu ve üyeleri düzenler", "Segmentler"),
        new(Permissions.SegmentsPublish, "Segmenti yayımlar, yeniler, arşivler ve geri alır", "Segmentler"),
        new(Permissions.RecipientsRead, "Segment üyelerini (firma/kişi adlarını) görüntüler", "Alıcılar"),
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
            [Permissions.SegmentsRead, Permissions.SegmentsWrite, Permissions.RecipientsRead])
        {
            Scope = RecordScope.Team,
        },
        new(
            "marketing.manager",
            "Pazarlama yöneticisi",
            "Bütün segmentleri yönetir, yayımlar ve arşivler.",
            [Permissions.SegmentsRead, Permissions.SegmentsWrite, Permissions.SegmentsPublish, Permissions.RecipientsRead])
        {
            Scope = RecordScope.All,
        },
        new(
            "marketing.viewer",
            "Pazarlama izleyici",
            "Segmentleri ve sayılarını görüntüler; alıcı listesini görmez.",
            [Permissions.SegmentsRead])
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
    }
}
