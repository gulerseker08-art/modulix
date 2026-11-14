using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>
/// Tamamı istemciye yüklenmiş (sayfalanmayan) küçük listelerin başlığa göre sıralaması. Sayfalanan listelerde sıralama
/// sunucudadır (<c>sort</c> anahtarı); bu yardımcı yalnızca bütün satırları elinde tutan ekranlar içindir. Anahtar biçimi
/// <c>SortHeader</c> ile aynıdır: <c>name</c> artan, <c>-name</c> azalan; boş ya da bilinmeyen anahtar listenin kendi sırasını korur.
/// </summary>
public static class ClientSort
{
    private static readonly CompareInfo Turkish = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    /// <summary>
    /// Listeyi verilen anahtara göre sıralar. Metinler Türkçe sıraya göre ve büyük/küçük harf ayırmadan karşılaştırılır,
    /// boş değerler her iki yönde de sonda kalır, eşit satırlar ilk sıralarını korur.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> items, string? sort, IReadOnlyDictionary<string, Func<T, object?>> columns)
    {
        List<T> list = [.. items];
        if (string.IsNullOrEmpty(sort))
        {
            return list;
        }

        bool descending = sort[0] == '-';
        if (!columns.TryGetValue(descending ? sort[1..] : sort, out Func<T, object?>? column))
        {
            return list;
        }

        return [.. list
            .Select((item, index) => (item, index, value: column(item)))
            .OrderBy(row => row.value is null or "" ? 1 : 0)
            .ThenBy(row => row.value, Comparer<object?>.Create((a, b) => descending ? Compare(b, a) : Compare(a, b)))
            .ThenBy(row => row.index)
            .Select(row => row.item)];
    }

    private static int Compare(object? a, object? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string x, string y) => Turkish.Compare(x, y, CompareOptions.IgnoreCase),
        (IComparable x, _) when a.GetType() == b.GetType() => x.CompareTo(b),
        _ => Turkish.Compare(a.ToString(), b.ToString(), CompareOptions.IgnoreCase),
    };
}
