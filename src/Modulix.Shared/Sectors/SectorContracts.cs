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
