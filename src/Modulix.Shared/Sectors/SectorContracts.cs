namespace Modulix.Shared.Sectors;

/// <summary>Sektör/faaliyet kataloğu düğümü (ADR-0029). <c>Kind</c>: Sector, Group, Activity; <c>Readiness</c>: CatalogOnly, Available.</summary>
public sealed record ActivityNodeDto(
    Guid Id,
    string Code,
    Guid? ParentId,
    string Kind,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    string Readiness,
    long Version,
    List<string> Capabilities);

public sealed record CapabilityDto(Guid Id, string Code, string Name, bool IsActive, List<string> DependsOn);

public sealed record SectorCatalogResponse(List<ActivityNodeDto> Nodes, List<CapabilityDto> Capabilities);

public sealed record CreateActivityNodeRequest(string Code, Guid? ParentId, string Kind, string Name, string? Description, int SortOrder);

public sealed record UpdateActivityNodeRequest(string Name, string? Description, int SortOrder, string Readiness, long Version);

public sealed record SetActiveRequest(bool Active);

public sealed record CodeListRequest(List<string> Codes);

public sealed record CreateCapabilityRequest(string Code, string Name);

public sealed record ActivityIdsRequest(List<Guid> NodeIds);

public sealed record CapabilityGapDto(string Capability, List<Guid> RequiredByNodes);

public sealed record SelectionPreviewDto(List<Guid> ContextNodeIds, List<string> Errors, List<CapabilityGapDto> Gaps, List<Guid> CatalogOnlyNodeIds, bool IsComplete);

public sealed record TenantActivitySelectionResponse(List<Guid> SelectedNodeIds, SelectionPreviewDto Preview);

/// <summary>Firma ilişki rolü kataloğu öğesi (müşteri, tedarikçi, taşıyıcı...); firma kaydı rolü koduyla tutar.</summary>
public sealed record RelationshipRoleDto(Guid Id, string Code, string Name, bool IsActive);

public sealed record CreateRelationshipRoleRequest(string Code, string Name);

/// <summary>Sektör kataloğu öğesi (ADR-0042). Firma kaydındaki sektör bu kodu taşır.</summary>
public sealed record IndustryDto(Guid Id, string Code, string Name, bool IsActive);

public sealed record CreateIndustryRequest(string Code, string Name);

/// <summary>Firma formunun seçim listesi için aktif faaliyet düğümü; yalnızca ad ve konum bilgisi taşır (ADR-0029).</summary>
public sealed record ActivityOptionDto(string Code, string Name, string Kind, string? ParentCode, int SortOrder);

public sealed record StarterPackageDto(string Code, string Name, string? Description, bool Verified, string? VerificationNote, int NodeCount, int CapabilityCount);

public sealed record StarterPackageRetirePlanDto(
    List<string> NodesToDeactivate,
    List<string> NodesAlreadyInactive,
    List<string> NodesMissing,
    List<string> Skipped,
    List<string> Warnings,
    bool NothingToDo);

public sealed record StarterPackagePlanDto(
    List<string> CapabilitiesToCreate,
    List<string> CapabilitiesExisting,
    List<string> NodesToCreate,
    List<string> NodesExisting,
    List<string> Warnings,
    List<string> Completions,
    bool NothingToDo);
