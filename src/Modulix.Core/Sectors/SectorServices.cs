namespace Modulix.Core.Sectors;

/// <summary>Katalog düğümü, yönetim görünümü. <see cref="Version"/> iyimser kilittir; güncellemede geri gönderilir.</summary>
public sealed record ActivityNodeView(
    Guid Id,
    string Code,
    Guid? ParentId,
    ActivityNodeKind Kind,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    ModuleReadiness Readiness,
    long Version,
    IReadOnlyList<string> Capabilities);

public sealed record CapabilityView(Guid Id, string Code, string Name, bool IsActive, IReadOnlyList<string> DependsOn);

public sealed record SectorCatalogView(IReadOnlyList<ActivityNodeView> Nodes, IReadOnlyList<CapabilityView> Capabilities);

public sealed record ActivityNodeInput(string Code, Guid? ParentId, ActivityNodeKind Kind, string Name, string? Description, int SortOrder);

/// <summary>Düğüm güncellemesi; kod ve üst düğüm değişmez (taşıma bu aşamada yoktur).</summary>
public sealed record ActivityNodeUpdate(string Name, string? Description, int SortOrder, ModuleReadiness Readiness, long Version);

public sealed record CapabilityInput(string Code, string Name);

/// <summary>Tenant seçimi: kayıtlı açık seçim ve türetilmiş önizleme.</summary>
public sealed record TenantActivitySelectionView(
    IReadOnlyList<Guid> SelectedNodeIds,
    SelectionPreview Preview);

/// <summary>Katalog kaydı bulunamadı.</summary>
public sealed class SectorCatalogNotFoundException(string message) : Exception(message);

/// <summary>Kod çakışması gibi veri durumundan doğan hata (409).</summary>
public sealed class SectorCatalogConflictException(string message) : Exception(message);

/// <summary>
/// Platform yöneticisinin sektör/faaliyet kataloğu yönetimi (ADR-0029). Katalog global ve RLS dışıdır; her yazma
/// <c>platform.PlatformAuditLog</c>'a aynı işlemde yazılır. Doğrulama hataları <see cref="ArgumentException"/> olarak atılır.
/// </summary>
public interface ISectorCatalog
{
    Task<SectorCatalogView> GetAsync(CancellationToken cancellationToken);

    Task<ActivityNodeView> CreateNodeAsync(Guid actingUserId, ActivityNodeInput input, CancellationToken cancellationToken);

    /// <summary>Eşzamanlı düzenleme çakışırsa DbUpdateConcurrencyException yerine <see cref="SectorCatalogConflictException"/> atar.</summary>
    Task<ActivityNodeView> UpdateNodeAsync(Guid actingUserId, Guid nodeId, ActivityNodeUpdate update, CancellationToken cancellationToken);

    /// <summary>Silmek yerine pasife alır/açar. Aktif alt düğümü olan düğüm pasife alınamaz.</summary>
    Task<ActivityNodeView> SetNodeActiveAsync(Guid actingUserId, Guid nodeId, bool active, CancellationToken cancellationToken);

    /// <summary>Düğümün gerektirdiği yetenek kodlarının tamamını değiştirir.</summary>
    Task<ActivityNodeView> SetNodeCapabilitiesAsync(Guid actingUserId, Guid nodeId, IReadOnlyCollection<string> capabilityCodes, CancellationToken cancellationToken);

    Task<CapabilityView> CreateCapabilityAsync(Guid actingUserId, CapabilityInput input, CancellationToken cancellationToken);

    /// <summary>Yeteneğin bağımlılıklarının tamamını değiştirir; döngü reddedilir.</summary>
    Task<CapabilityView> SetCapabilityDependenciesAsync(Guid actingUserId, string capabilityCode, IReadOnlyCollection<string> dependsOn, CancellationToken cancellationToken);
}

/// <summary>
/// Tenant'ın faaliyet seçimi (ADR-0029 K3/K4). Tenant bağlamında çalışır; seçim RLS altındaki <c>core.ActivitySelections</c>'dadır
/// ve <c>core.activity-selection</c> olarak denetime yazılır. Boş seçim geçerlidir (eski tenant'lar değişmez).
/// </summary>
public interface ITenantActivitySelection
{
    Task<TenantActivitySelectionView> GetAsync(CancellationToken cancellationToken);

    /// <summary>Kaydetmeden önizler: üst bağlam, pasif/bilinmeyen düğüm hataları, eksik yetenekler.</summary>
    Task<SelectionPreview> PreviewAsync(IReadOnlyCollection<Guid> nodeIds, CancellationToken cancellationToken);

    /// <summary>Seçimin tamamını değiştirir. Hata içeren seçim kaydedilmez (<see cref="ArgumentException"/>); eksik yetenek uyarıdır, engel değildir.</summary>
    Task<TenantActivitySelectionView> SaveAsync(IReadOnlyCollection<Guid> nodeIds, CancellationToken cancellationToken);
}
