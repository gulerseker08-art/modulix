namespace Modulix.Modules.Marketing.Domain;

// Kampanya ve hedef kitle entity'leri (ADR-0025, dilim 4). Kampanya, segmentin kendisini değiştirmez; hedef kitle segmentlerden ve elle seçilen
// kayıtlardan oluşur, çalıştırma öncesi dondurulur (AudienceSnapshot) ve izin/engel kontrolü gönderimden önce yeniden yapılır.

public static class CampaignStatuses
{
    public const string Draft = "Draft";

    /// <summary>Arşivlenen kampanya geçmişte kalır; yeni hedef kitle dondurulamaz.</summary>
    public const string Archived = "Archived";

    public static IReadOnlyList<string> All { get; } = [Draft, Archived];
}

public static class AudienceModes
{
    public const string Include = "Include";

    /// <summary>Hariç tutma, dahil etmenin önüne geçer.</summary>
    public const string Exclude = "Exclude";

    public static IReadOnlyList<string> All { get; } = [Include, Exclude];
}

/// <summary>
/// Kampanya: amaç, dönem, sorumlu ve hedef kitle kuralı. Hedef türü (firma/kişi/lead) ve kanal oluşturulduktan sonra değişmez; karışık türler tek
/// kitlede birleştirilmez. İçerik, onay, planlama ve gönderim sonraki dilimlerdedir.
/// </summary>
public sealed class Campaign : MarketingEntity, IOwnedEntity
{
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 1000;
    public const int PurposeMaxLength = 200;
    public const int RoleMaxLength = 100;
    public const int UtmMaxLength = 80;

    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Kampanyanın amacı (ör. demo talebi, çapraz satış); katalog yönetimi sonraki dilimde.</summary>
    public string? Purpose { get; internal set; }

    public string Target { get; internal set; } = SegmentTargets.Company;

    /// <summary>Email, Phone, Sms ya da WhatsApp (CommunicationChannel adı).</summary>
    public string Channel { get; internal set; } = "Email";

    /// <summary>Firma hedefinde kişi seçme kuralı (RecipientRule adı); kişi ve lead hedefinde kullanılmaz.</summary>
    public string RecipientRule { get; internal set; } = "PrimaryContact";

    /// <summary>Yalnızca <c>Role</c> kuralında: aranan ilişki rolü.</summary>
    public string? RecipientRole { get; internal set; }

    /// <summary>Hedef başına en çok alıcı; boşsa sınır yok.</summary>
    public int? MaxPerTarget { get; internal set; }

    public DateOnly? StartsOn { get; internal set; }

    public DateOnly? EndsOn { get; internal set; }

    /// <summary>Kaynak/UTM (ADR-0025, dilim 7): yanıttan açılan lead'e geçer. Boşsa varsayılan: kaynak "kampanya", ortam kanal, kampanya kodu.</summary>
    public string? UtmSource { get; internal set; }

    public string? UtmMedium { get; internal set; }

    public string? UtmCampaign { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string Status { get; internal set; } = CampaignStatuses.Draft;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>Hedef kitleye dahil/hariç giren bir segment ya da elle seçilen hedef. Tam olarak biri dolu: <see cref="SegmentId"/> ya da <see cref="TargetId"/>.</summary>
public sealed class CampaignAudienceEntry : MarketingEntity
{
    public const int ReasonMaxLength = 200;

    public Guid CampaignId { get; internal set; }

    public string Mode { get; internal set; } = AudienceModes.Include;

    public Guid? SegmentId { get; internal set; }

    public Guid? TargetId { get; internal set; }

    public string? Reason { get; internal set; }

    public Guid? AddedBy { get; internal set; }
}

/// <summary>
/// Dondurulmuş hedef kitle (değişmez). Hedefler ve alıcı seçimi o ana göredir; sonradan segment ya da izin değişse bu kayıt değişmez. Kişisel veri
/// kopyalanmaz: yalnızca hedef/kişi/iletişim noktası kimlikleri ve kodlar saklanır; adres gönderimden önce yeniden çözülür ve izin yeniden denetlenir.
/// </summary>
public sealed class AudienceSnapshot : MarketingEntity
{
    public const int RecipientsMaxRows = 50000;

    public Guid CampaignId { get; internal set; }

    public string Target { get; internal set; } = SegmentTargets.Company;

    public string Channel { get; internal set; } = "Email";

    public DateTimeOffset TakenAt { get; internal set; }

    public Guid? TakenBy { get; internal set; }

    public int TargetCount { get; internal set; }

    public int ResolvedTargetCount { get; internal set; }

    public int PersonCount { get; internal set; }

    public int UniqueAddressCount { get; internal set; }

    public int EligibleCount { get; internal set; }

    public int ExcludedCount { get; internal set; }

    /// <summary>JSON: elenen alıcıların neden → sayı dağılımı.</summary>
    public string Exclusions { get; internal set; } = "{}";

    /// <summary>JSON: alıcısı bulunamayan hedeflerin neden → sayı dağılımı.</summary>
    public string Unresolved { get; internal set; } = "{}";

    /// <summary>JSON: hedefe katkıda bulunan kaynaklar (segment kimliği, sürüm, dahil/hariç, sayı) ve elle seçilenlerin sayısı.</summary>
    public string Sources { get; internal set; } = "[]";

    /// <summary>JSON dizisi: <c>{t: hedef, p: kişi, c: iletişim noktası, s: E|X|U, x: kod}</c> (E uygun, X elendi, U alıcı yok).</summary>
    public string Recipients { get; internal set; } = "[]";
}
