namespace Modulix.Shared.Marketing;

// Kampanya ve hedef kitle (ADR-0025, dilim 4). Kanal: Email, Phone, Sms, WhatsApp. Kişi seçme kuralı (yalnızca firma hedefinde):
// PrimaryContact, DecisionMaker, Role, AllContacts, CompanyPoints. Durum: Draft, Archived. Hedef kitle satırı durumu: Eligible, Excluded, Unresolved.

public sealed record CampaignDto(
    Guid Id,
    long Version,
    string Code,
    string Name,
    string? Description,
    string? Purpose,
    string Target,
    string Channel,
    string RecipientRule,
    string? RecipientRole,
    int? MaxPerTarget,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    Guid? OwnerId,
    string Status,
    Guid? CreatedBy,
    Guid? UpdatedBy,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    decimal? Budget = null,
    decimal? ActualCost = null,
    string? CostCurrency = null,
    string? Topic = null);

/// <summary>Kod, hedef türü ve kanal oluşturulduktan sonra değiştirilemez (PUT için aynı değerler gönderilir).</summary>
public sealed record SaveCampaignRequest(
    long Version = 0,
    string? Code = null,
    string? Name = null,
    string? Description = null,
    string? Purpose = null,
    string? Target = null,
    string? Channel = null,
    string? RecipientRule = null,
    string? RecipientRole = null,
    int? MaxPerTarget = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    Guid? OwnerId = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    decimal? Budget = null,
    decimal? ActualCost = null,
    string? CostCurrency = null,
    string? Topic = null);

/// <summary>Hedef kitle girdisi: <c>SegmentId</c> ya da <c>TargetId</c>'den tam biri (hedef: taraf, ortak kişi ya da lead kimliği). Mod: Include, Exclude.</summary>
public sealed record CampaignAudienceEntryDto(Guid Id, long Version, Guid CampaignId, string Mode, Guid? SegmentId, Guid? TargetId, string? Reason, Guid? AddedBy);

public sealed record SaveCampaignAudienceEntryRequest(
    long Version = 0, Guid? CampaignId = null, string? Mode = null, Guid? SegmentId = null, Guid? TargetId = null, string? Reason = null);

/// <summary>Dondurulmuş hedef kitle özeti. <c>Exclusions</c>, <c>Unresolved</c> ve <c>Sources</c> JSON metindir (neden → sayı, neden → sayı, kaynak listesi).</summary>
public sealed record AudienceSnapshotDto(
    Guid Id,
    long Version,
    Guid CampaignId,
    string Target,
    string Channel,
    DateTimeOffset TakenAt,
    Guid? TakenBy,
    int TargetCount,
    int ResolvedTargetCount,
    int PersonCount,
    int UniqueAddressCount,
    int EligibleCount,
    int ExcludedCount,
    string Exclusions,
    string Unresolved,
    string Sources);

/// <summary>Dondurulmuş hedef kitle yalnızca hedef kitle eylemiyle oluşur; bu istek biçimi genel uçların gövdesidir ve kabul edilmez.</summary>
public sealed record SaveAudienceSnapshotRequest(long Version = 0);

/// <summary>
/// Sayıların anlamı: <c>Targets</c> hedef firma/kişi/lead; <c>ResolvedTargets</c> alıcısı bulunan hedef; <c>Persons</c> çözümlenen farklı kişi;
/// <c>UniqueAddresses</c> benzersiz iletişim noktası; <c>Eligible</c> uygun alıcı; <c>Excluded</c> elenen alıcı. Hedef başına birden çok kişi olabildiği için
/// alıcı sayısı hedef sayısından farklıdır. <c>Exclusions</c> elenen alıcıların, <c>Unresolved</c> alıcısı bulunamayan hedeflerin neden dağılımıdır.
/// </summary>
public sealed record AudienceSummaryDto(
    int Targets,
    int ResolvedTargets,
    int Persons,
    int UniqueAddresses,
    int Eligible,
    int Excluded,
    IReadOnlyDictionary<string, int> Exclusions,
    IReadOnlyDictionary<string, int> Unresolved);

public sealed record AudienceSourceDto(Guid? SegmentId, string? Name, string Mode, int Count);

public sealed record AudiencePreviewDto(AudienceSummaryDto Summary, IReadOnlyList<AudienceSourceDto> Sources);

public sealed record AudienceRecipientDto(Guid TargetId, string? Label, Guid? PersonId, string Status, string? Code);

public sealed record AudienceRecipientsPageDto(IReadOnlyList<AudienceRecipientDto> Items, int Page, int PageSize, int TotalCount);
