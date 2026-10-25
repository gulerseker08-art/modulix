namespace Modulix.Core.Contracts.Customization;

/// <summary>
/// Aktif tenant'ın seçim listeleri (Y9b): kodun varsayılanı ile firmanın düzenlemeleri birleşmiş hali. Kayıt depoları değeri
/// doğrulamak ve kategoriye göre süzmek için kullanır. İstek boyunca saklanabilir.
/// </summary>
public interface IChoiceLists
{
    /// <summary>Tanımsız liste anahtarı için hata fırlatır.</summary>
    Task<ChoiceList> GetAsync(string listKey, CancellationToken cancellationToken);
}

/// <summary>Firmanın listesi, sıraya göre. Pasif değerler de listededir: onları taşıyan kayıtlar anlamını korur.</summary>
/// <param name="PreferredDefault">Tanımın varsayılan değeri (<see cref="Modules.ChoiceListDefinition.Default"/>).</param>
public sealed record ChoiceList(string Key, IReadOnlyList<ChoiceValue> Values, string? PreferredDefault = null)
{
    /// <summary>Anahtar büyük/küçük harfe duyarsız aranır (veritabanı da öyle karşılaştırır); kayda değerin kendi anahtarı yazılır.</summary>
    public ChoiceValue? Find(string key) => Values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Yeni kayıtta verilmeyen zorunlu alanın değeri: tanımın varsayılanı etkinse o, değilse ilk etkin değer.</summary>
    public ChoiceValue? DefaultValue =>
        (PreferredDefault is { } preferred && Find(preferred) is { Active: true } value ? value : null) ?? Values.FirstOrDefault(v => v.Active);

    /// <summary>Kategorinin bütün değerleri (pasifler dahil), ör. açık fırsat süzgeci için.</summary>
    public IReadOnlyList<string> KeysIn(string category) => [.. Values.Where(v => v.Category == category).Select(v => v.Key)];
}

/// <param name="BuiltIn">Kodda tanımlı hazır değer; firmanın eklediği değerlerde false.</param>
public sealed record ChoiceValue(string Key, string Label, string? Category, bool Active, bool BuiltIn);

/// <summary>Değer anahtarı: harfle başlar; harf, rakam ve alt çizgi (ör. "Proposal", "demo_yapildi").</summary>
public static class ChoiceValueKeys
{
    public const int MaxLength = 40;

    public static bool IsValid(string? key) =>
        key is { Length: > 0 and <= MaxLength } && char.IsAsciiLetter(key[0]) && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
