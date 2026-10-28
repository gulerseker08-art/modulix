using System.Text.Json.Nodes;

namespace Modulix.Shared.Crm;

// Mükerrer önerisi ve birleştirme (ADR-0013).

/// <summary>
/// Birleştirme isteği; hedef rotadadır (<c>/api/crm/companies/{targetId}/merge</c>). Her iki sürüm zorunludur: ekranın gördüğü
/// hal. <see cref="FieldChoices"/>: alan adı → "source" veya "target" (verilmeyen alan hedeften kalır; özel alan "custom.anahtar").
/// </summary>
public sealed record MergeRecordsRequest(
    Guid SourceId,
    long SourceVersion,
    long TargetVersion,
    Dictionary<string, string>? FieldChoices = null);

public sealed record MergeFieldDiffDto(string Field, JsonNode? Source, JsonNode? Target, string Suggested);

public sealed record MergeImpactDto(string Label, int Count);

/// <summary>Önizleme. <see cref="Blockers"/> doluysa birleştirme yapılamaz.</summary>
public sealed record MergePreviewDto(
    Guid SourceId,
    Guid TargetId,
    long SourceVersion,
    long TargetVersion,
    string SourceName,
    string TargetName,
    IReadOnlyList<MergeFieldDiffDto> Fields,
    IReadOnlyList<MergeImpactDto> Impacts,
    IReadOnlyList<string> Blockers);

public sealed record MergeResultDto(Guid TargetId, long TargetVersion, Guid SourceId, IReadOnlyList<MergeImpactDto> Impacts);

/// <summary>Birleştirilmiş eski kimliğin gittiği kayıt.</summary>
public sealed record MergedRecordDto(Guid SourceId, Guid TargetId, DateTimeOffset MergedAt);

/// <summary>Eşleşme türleri.</summary>
public static class CrmMatchKinds
{
    /// <summary>Kesin eşleşme: vergi numarası. Yeni kayıt açmak yerine mevcut kayıt kullanılmalıdır.</summary>
    public const string Exact = "Exact";

    /// <summary>Olası eşleşme: ad, e-posta, telefon benzerliği. Otomatik birleştirilmez; kararı kullanıcı verir.</summary>
    public const string Similar = "Similar";
}

/// <summary>
/// Mükerrer adayı. <see cref="Id"/>: CRM kaydı (çağıranın kapsamında); yoksa kayıt başka bir modülde (ortak kayıt) ya da
/// kapsam dışındadır, o zaman yalnızca <see cref="SharedId"/> verilir ve "mevcut kaydı kullan" isteği (partyId/personId)
/// sunucuda yeniden denetlenir. <see cref="Reasons"/> eşleşmenin nedenleridir.
/// </summary>
public sealed record DuplicateCandidateDto(
    Guid? Id,
    Guid? SharedId,
    string Name,
    string? TaxNumber,
    string? City,
    string? Email,
    string? Phone,
    string Match,
    IReadOnlyList<string> Reasons);
