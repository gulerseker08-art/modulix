using System.Text.Json.Nodes;

namespace Modulix.Shared.Records;

// Genel kayıt uç noktalarının (/api/records/{entityKey}) sözleşmeleri. Alan adları teknik kimliklerdir.

/// <summary>Oluşturma ve güncelleme gövdesi. Güncellemede <see cref="Version"/> verilirse eşzamanlı değişiklik 409 ile reddedilir.</summary>
public sealed record RecordWriteRequest(JsonObject? Values, long? Version = null);

public sealed record RecordResponse(string EntityKey, Guid Id, long Version, JsonObject Values);

public sealed record RecordPageResponse(IReadOnlyList<RecordResponse> Items, int Page, int PageSize, int TotalCount);
