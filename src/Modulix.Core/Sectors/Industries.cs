namespace Modulix.Core.Sectors;

public sealed record IndustryView(Guid Id, string Code, string Name, bool IsActive);

/// <summary>Sektör kataloğu yönetimi (platform tarafı, ADR-0042); yazmalar platform denetim kaydına aynı işlemde yazılır.</summary>
public interface IIndustries
{
    Task<IReadOnlyList<IndustryView>> ListAsync(CancellationToken cancellationToken);

    Task<IndustryView> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken);

    Task<IndustryView> SetActiveAsync(Guid actingUserId, string code, bool active, CancellationToken cancellationToken);
}
