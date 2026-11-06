namespace Modulix.Shared.Customs;

/// <summary>Tescil numarası kuralı (ADR-0032 K7). <c>Enforcement</c>: <c>Warn</c> (uyarı, kaydı engellemez) ya da <c>Block</c> (kaydı reddeder). Desen numaranın tamamıyla eşleşir.</summary>
public sealed record RegistrationNoRuleDto(string? Pattern, int? MinLength, int? MaxLength, string? Example, string Enforcement);

/// <summary>Rejim profili içeriği (K16). Bölümü olmayan kural uygulanmaz.</summary>
public sealed record RegimeProfileContentDto(int SchemaVersion, string? RegimeCode, RegistrationNoRuleDto? RegistrationNo);

public sealed record RegimeProfileSummaryDto(Guid Id, string Code, string Name, bool IsActive, int? PublishedNumber, bool PublishedVerified, bool HasDraft);

/// <summary><c>State</c>: <c>Draft</c>, <c>Published</c>, <c>Superseded</c>.</summary>
public sealed record RegimeProfileVersionDto(
    Guid Id, int Number, string State, bool IsVerified, string? VerificationNote, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt,
    RegimeProfileContentDto Content);

public sealed record RegimeProfileDetailDto(RegimeProfileSummaryDto Summary, List<RegimeProfileVersionDto> Versions);

public sealed record CreateRegimeProfileRequest(string Code, string Name);

/// <param name="Verified">Kural uzman tarafından doğrulandı; doğrulanmışsa kimin/neye göre doğruladığı <paramref name="Note"/> olarak yazılır.</param>
public sealed record PublishRegimeProfileRequest(bool Verified, string? Note);
