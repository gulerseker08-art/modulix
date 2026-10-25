using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Customization;

/// <summary>Yeni ya da düzenlenen alanın seçeneği; <see cref="Key"/> boşsa addan üretilir (yeni seçenek).</summary>
public sealed record CustomFieldOptionInput(string? Key, string Label, bool Active = true);

/// <summary>Alan tanımı girdisi. Tip yalnızca oluştururken kullanılır, sonra değişmez.</summary>
public sealed record CustomFieldInput(
    string Label,
    CustomFieldType Type,
    bool Required,
    bool ShowInList,
    IReadOnlyList<CustomFieldOptionInput>? Options = null);

/// <summary>
/// Aktif tenant'ın özel alan tanımları (Y9a). Yazma işlemleri <c>core.customization.manage</c> ister (API denetler).
/// Geçersiz girdi için <see cref="ArgumentException"/> (ParamName alan adıdır). Değişiklikler <c>core.custom-field</c>
/// kaydı olarak denetim kaydına yazılır.
/// </summary>
public interface ICustomFieldService
{
    /// <summary>Özel alan eklenebilen, modülü tenant için açık kayıt türleri.</summary>
    Task<IReadOnlyList<CustomizableEntity>> ListEntitiesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sıraya göre alanlar. <paramref name="entityKey"/> null ise açık modüllerin tüm türleri. Arşivlenenler yalnızca
    /// istenirse gelir.
    /// </summary>
    Task<IReadOnlyList<CustomFieldInfo>> ListAsync(string? entityKey, bool includeArchived, CancellationToken cancellationToken);

    Task<CustomFieldInfo> CreateAsync(string entityKey, CustomFieldInput input, CancellationToken cancellationToken);

    /// <summary>Alan yoksa null. Ad, zorunluluk, listede gösterme ve seçenekler değişir; tip ve anahtar değişmez.</summary>
    Task<CustomFieldInfo?> UpdateAsync(Guid id, CustomFieldInput input, CancellationToken cancellationToken);

    /// <summary>Alan yoksa null.</summary>
    Task<CustomFieldInfo?> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken);

    /// <summary>Türün alanlarını verilen sıraya koyar; listede olmayanlar sona kalır.</summary>
    Task ReorderAsync(string entityKey, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}
