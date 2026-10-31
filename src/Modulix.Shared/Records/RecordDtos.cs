using System.Text.Json.Nodes;

namespace Modulix.Shared.Records;

// Genel kayıt uç noktalarının (/api/records/{entityKey}) sözleşmeleri. Alan adları teknik kimliklerdir.

/// <summary>Oluşturma ve güncelleme gövdesi. Güncellemede <see cref="Version"/> zorunludur (yoksa 428); değişmiş sürüm 409 ile reddedilir.</summary>
public sealed record RecordWriteRequest(JsonObject? Values, long? Version = null);

public sealed record RecordResponse(string EntityKey, Guid Id, long Version, JsonObject Values);

public sealed record RecordPageResponse(IReadOnlyList<RecordResponse> Items, int Page, int PageSize, int TotalCount);

/// <summary>Modül kaynaklarının sayfalı listesi (CRM'deki <c>CrmPage&lt;T&gt;</c> ile aynı biçim).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
