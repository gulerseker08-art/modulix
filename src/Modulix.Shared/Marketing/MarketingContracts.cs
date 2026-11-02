using System.Text.Json.Nodes;

namespace Modulix.Shared.Marketing;

// Pazarlama modülü API sözleşmeleri (/api/marketing, ADR-0025). Durum, yayın sürümü ve hesaplama özeti sunucu tarafından yazılır; genel kayıt isteğinde
// gönderilse bile yok sayılır. Hedef türü: Company, Person, Lead. Tür: Static, Dynamic. Durum: Draft, Active, Archived. Üye türü: Include, Exclude.

public sealed record SegmentDto(
    Guid Id,
    long Version,
    string Code,
    string Name,
    string? Description,
    string Target,
    string Kind,
    string? Purpose,
    Guid? OwnerId,
    string Status,
    string? Draft,
    int PublishedVersion,
    DateTimeOffset? LastComputedAt,
    int? LastMemberCount,
    int? LastComputeMs,
    string? LastError,
    Guid? CreatedBy,
    Guid? UpdatedBy);

/// <summary>Kod, hedef türü ve tür oluşturulduktan sonra değiştirilemez (PUT'ta aynı değerler gönderilir). <c>Draft</c> koşul ağacının JSON metnidir.</summary>
public sealed record SaveSegmentRequest(
    long Version = 0,
    string? Code = null,
    string? Name = null,
    string? Description = null,
    string? Target = null,
    string? Kind = null,
    string? Purpose = null,
    Guid? OwnerId = null,
    string? Draft = null);

public sealed record SegmentMemberDto(Guid Id, long Version, Guid SegmentId, Guid TargetId, string Kind, string? Reason, Guid? AddedBy);

public sealed record SaveSegmentMemberRequest(long Version = 0, Guid? SegmentId = null, Guid? TargetId = null, string? Kind = null, string? Reason = null);

public sealed record SegmentVersionDto(Guid Id, long Version, Guid SegmentId, int VersionNo, string Definition, DateTimeOffset PublishedAt, Guid? PublishedBy);

/// <summary>Yayımlanmış sürüm yalnızca yayım eylemiyle oluşur; bu istek biçimi genel uçların gövdesidir ve kabul edilmez.</summary>
public sealed record SaveSegmentVersionRequest(long Version = 0);

public sealed record SegmentFieldDto(
    string Key,
    string Label,
    string Group,
    string Target,
    string Type,
    IReadOnlyList<string> Operators,
    IReadOnlyDictionary<string, string> Options,
    string? Hint);

public sealed record UnavailableSegmentFieldDto(string Key, string Label, string Reason);

/// <summary>Çağıranın kullanabildiği alanlar ve kullanamadıkları (modül kapalı ya da okuma yetkisi yok).</summary>
public sealed record SegmentFieldsResponse(IReadOnlyList<SegmentFieldDto> Fields, IReadOnlyList<UnavailableSegmentFieldDto> Unavailable);

/// <summary>
/// Önizleme: <c>Condition</c> koşul ağacı nesnesidir — grup <c>{"op":"and|or","items":[…]}</c>, koşul <c>{"field","operator","values":[…]}</c>.
/// Operatörler: eq, ne, contains, in, between, empty, notempty, lastdays, nextdays, before, after, exists, notexists.
/// </summary>
public sealed record PreviewSegmentRequest(string Target, JsonObject? Condition, int Page = 1, int PageSize = 25);

public sealed record SegmentRowDto(Guid Id, string? Label);

/// <summary><c>RowsHidden</c>: satırlar alıcı görüntüleme yetkisi olmadığı için gizlendi (sayı yine verilir).</summary>
public sealed record SegmentEvaluationDto(int Count, IReadOnlyList<SegmentRowDto> Rows, bool RowsHidden, int DurationMs);

/// <summary>Eylem isteği: kaydın sürümü verilirse eşzamanlılık denetlenir (409).</summary>
public sealed record SegmentActionRequest(long? Version = null);

public sealed record CopySegmentRequest(string Code, string Name);
