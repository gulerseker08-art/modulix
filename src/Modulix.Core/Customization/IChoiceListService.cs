using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Customization;

/// <summary>Firmanın listesi ve tanımı; <see cref="Customized"/> firma listeyi düzenlemişse true.</summary>
public sealed record ChoiceListInfo(ChoiceListDefinition Definition, ChoiceList List, bool Customized);

/// <summary>Seçim listelerinin yönetimi (Y9b), aktif tenant için. Yalnızca açık modüllerin listeleri görünür ve düzenlenir.</summary>
public interface IChoiceListService
{
    Task<IReadOnlyList<ChoiceListInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Tanımsız veya kapalı modülün listesi için null.</summary>
    Task<ChoiceListInfo?> SaveAsync(string listKey, IReadOnlyList<ChoiceValueInput> values, CancellationToken cancellationToken);
}
