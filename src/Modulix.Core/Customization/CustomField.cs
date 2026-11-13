using System.Text.Json;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Customization;

public enum CustomFieldType
{
    Text = 1,
    MultilineText = 2,
    Number = 3,
    Date = 4,
    YesNo = 5,
    Choice = 6,
    MultiChoice = 7,
}

/// <summary>Seçim tipli özel alanın seçeneği. <see cref="Key"/> değişmez; kayıtlarda bu saklanır.</summary>
public sealed record CustomFieldOption(string Key, string Label, bool Active = true);

/// <summary>
/// Firmanın bir kayıt türüne eklediği alan (Y9a, ör. fırsatta "Araç tipi"). Anahtar ve tip kaydedildikten sonra değişmez,
/// çünkü kayıtlardaki değerler buna bağlıdır. Alan silinmez, arşivlenir: değerler kayıtlarda kalır, geri alınabilir.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class CustomField : ITenantEntity, ICompanyShared
{
    public const int LabelMaxLength = 100;
    public const int KeyMaxLength = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private CustomField()
    {
        // EF Core için
    }

    public CustomField(string entityKey, string key, string label, CustomFieldType type, int sortOrder)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentException("Geçersiz alan tipi.", nameof(type));
        }

        Id = Guid.CreateVersion7();
        EntityKey = entityKey;
        Key = key;
        Type = type;
        SortOrder = sortOrder;
        Relabel(label);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    public string Key { get; private set; } = string.Empty;

    public string Label { get; private set; } = string.Empty;

    public CustomFieldType Type { get; private set; }

    public bool Required { get; set; }

    public bool ShowInList { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Seçenekler JSON olarak saklanır; <see cref="Options"/> üzerinden okunur ve yazılır.</summary>
    public string OptionsJson { get; private set; } = "[]";

    public bool IsArchived => ArchivedAt is not null;

    public bool HasOptions => Type is CustomFieldType.Choice or CustomFieldType.MultiChoice;

    public IReadOnlyList<CustomFieldOption> Options
    {
        get => JsonSerializer.Deserialize<List<CustomFieldOption>>(OptionsJson, Json) ?? [];
        set => OptionsJson = JsonSerializer.Serialize(value, Json);
    }

    public void Relabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Alan adı zorunludur.", "label");
        }

        var trimmed = label.Trim();
        if (trimmed.Length > LabelMaxLength)
        {
            throw new ArgumentException($"Alan adı en fazla {LabelMaxLength} karakter olabilir.", "label");
        }

        Label = trimmed;
    }

    public void Archive() => ArchivedAt ??= DateTimeOffset.UtcNow;

    public void Restore() => ArchivedAt = null;

    public CustomFieldInfo ToInfo() =>
        new(Id, EntityKey, Key, Label, Type, Required, ShowInList, SortOrder, IsArchived, HasOptions ? Options : []);
}

/// <summary>Özel alan tanımı; boru hattı, ekranlar ve denetim kaydı bunu kullanır.</summary>
public sealed record CustomFieldInfo(
    Guid Id,
    string EntityKey,
    string Key,
    string Label,
    CustomFieldType Type,
    bool Required,
    bool ShowInList,
    int SortOrder,
    bool Archived,
    IReadOnlyList<CustomFieldOption> Options);
