using System.Text.Json;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Customization;

/// <summary>Firmanın listedeki bir değeri, saklandığı biçimiyle. Sıra listedeki sıradır.</summary>
public sealed record StoredChoiceValue(string Key, string Label, string? Category, bool Active);

/// <summary>
/// Firmanın düzenlediği seçim listesi (Y9b). Yalnızca düzenlenen liste yazılır; satırı olmayan listede koddaki varsayılan
/// geçerlidir (<see cref="ChoiceListRules.Resolve"/>). Değerler silinmez, pasifleşir: kayıtlarda anahtarları durur.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class TenantChoiceList : ITenantEntity
{
    public const int ListKeyMaxLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private TenantChoiceList()
    {
        // EF Core için
    }

    public TenantChoiceList(string listKey)
    {
        Id = Guid.CreateVersion7();
        ListKey = listKey;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string ListKey { get; private set; } = string.Empty;

    /// <summary>Değerler JSON olarak, sırasıyla saklanır; <see cref="Values"/> üzerinden okunur ve yazılır.</summary>
    public string ValuesJson { get; private set; } = "[]";

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<StoredChoiceValue> Values
    {
        get => JsonSerializer.Deserialize<List<StoredChoiceValue>>(ValuesJson, Json) ?? [];
        set
        {
            ValuesJson = JsonSerializer.Serialize(value, Json);
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
