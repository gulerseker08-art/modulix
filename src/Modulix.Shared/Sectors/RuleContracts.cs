using System.Text.Json;

namespace Modulix.Shared.Sectors;

/// <summary>
/// Alan kuralı (ADR-0029 K7). <c>Effect</c>: Show, Hide, ReadOnly, Required, SuggestDefault, Validate, Warn, RequireApproval, Block.
/// <c>Condition</c> ADR-0027 koşul ağacının JSON'udur (boşsa kural her kayıt için geçerlidir).
/// </summary>
public sealed record FieldRuleDto(string Key, string Field, string Effect, JsonElement? Condition, string? Message, string? Value, bool Locked);

public sealed record RulePackageVersionDto(int Number, string Status, List<FieldRuleDto> Rules, string? Note, long Revision, DateTimeOffset? PublishedAt);

/// <summary><c>Layer</c>: ProductStandard (tüm firmalar) ya da SectorPackage (<c>NodeId</c> faaliyetini seçen firmalar).</summary>
public sealed record RulePackageDto(Guid Id, string Code, string Name, string Layer, Guid? NodeId, RulePackageVersionDto? Published, RulePackageVersionDto? Draft);

public sealed record CreateRulePackageRequest(string Code, string Name, string Layer, Guid? NodeId);

/// <summary><c>ExpectedRevision</c> açık bir taslak varsa gerekir (eşzamanlı düzenleme 409 döner).</summary>
public sealed record SaveRuleDraftRequest(List<FieldRuleDto> Rules, string? Note, long? ExpectedRevision);

public sealed record RuleIssueDto(string Path, string Message);

public sealed record RulePublishPreviewDto(
    List<RuleIssueDto> Issues, List<RuleIssueDto> Conflicts, List<RuleIssueDto> Warnings, List<string> Added, List<string> Removed, List<string> Changed, bool CanPublish);

public sealed record RollbackRulePackageRequest(int ToNumber);

public sealed record TenantRuleAdaptationDto(List<FieldRuleDto> Rules, List<string> DisabledRuleKeys, long Revision);

public sealed record SaveRuleAdaptationRequest(List<FieldRuleDto> Rules, List<string> DisabledRuleKeys, long? ExpectedRevision);

/// <summary>Firmanın etkin kuralları (ürün standardı + sektör paketleri + firma uyarlaması). Çakışma varsa yayın/kayıt reddedilirdi; burada gösterilir.</summary>
public sealed record EffectiveRulesDto(List<FieldRuleDto> Rules, List<RuleIssueDto> Conflicts, List<RuleIssueDto> Warnings);
