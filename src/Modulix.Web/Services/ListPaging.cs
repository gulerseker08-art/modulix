namespace Modulix.Web.Services;

/// <summary>
/// Liste ekranlarının sayfa boyutu (Pager'daki "25 / sayfa" seçici). Liste sayfaları sorgusuna <see cref="Size"/> değerini
/// <c>pageSize</c> olarak ekler; seçim oturum boyunca bütün listelerde ortaktır.
/// </summary>
public static class ListPaging
{
    /// <summary>Seçilebilir sayfa boyutları (sunucu üst sınırı aşan değeri kırpar).</summary>
    public static IReadOnlyList<int> Options { get; } = [10, 25, 50, 100];

    public static int Size { get; private set; } = 25;

    public static void Set(int size)
    {
        if (Options.Contains(size))
        {
            Size = size;
        }
    }
}
