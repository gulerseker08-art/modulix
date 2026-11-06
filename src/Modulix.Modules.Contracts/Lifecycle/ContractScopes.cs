using Modulix.Core.Contracts.Customization;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>
/// Sözleşme kapsamı: "Satış sözleşmeleri" gibi görünümler türün adına değil, firmanın sözleşme türü listesindeki <b>kategoriye</b> bakar.
/// Böylece firma kendi türlerini (ör. "Bayilik sözleşmesi") satış kategorisinde açar; satın alma ve personel sözleşmeleri satış görünümüne karışmaz.
/// Kod türün adını bilmez (sektör koşulu yok). Kategorisi olmayan eski özel tür hiçbir kapsamda sayılmaz; Listeler ekranından kategori atanınca girer.
/// Kapsam yalnızca bir süzgeçtir: görme yetkisi sağlamaz, kayıt erişimi (kapsam, hassasiyet) her sorguda yeniden uygulanır.
/// </summary>
public static class ContractScopes
{
    /// <summary>Liste süzgecinin adı (<c>typeCategory=sales</c>); sunucu tür anahtarlarına çevirir.</summary>
    public const string Filter = "typeCategory";

    public static IReadOnlyList<string> Categories { get; } =
    [
        ContractsModule.TypeCategories.Sales,
        ContractsModule.TypeCategories.Purchase,
        ContractsModule.TypeCategories.Employment,
        ContractsModule.TypeCategories.Other,
    ];

    public static bool IsKnown(string? category) => category is not null && Categories.Contains(category, StringComparer.Ordinal);

    /// <summary>Kategoriye bağlı türlerin anahtarları; pasif türler dahildir (eski kayıtlar o türü taşıyabilir).</summary>
    public static IReadOnlyList<string> TypeKeys(ChoiceList list, string category)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.KeysIn(category);
    }
}
