using Modulix.Shared.Views;

namespace Modulix.Web.Components;

/// <summary>Liste sütununun tanımı: anahtar (kişisel görünümde saklanır), başlık, tür ikonu ve sıralama anahtarları (yoksa sıralanamaz).</summary>
/// <param name="Locked">Her zaman görünür (ör. kaydın adı); kullanıcı gizleyemez.</param>
/// <param name="DefaultVisible">Varsayılan görünümde görünür mü; değilse sütun seçicide "ekle" ile açılır.</param>
public sealed record GridColumnDef(string Key, string Label, string Icon, string? SortAsc = null, string? SortDesc = null, bool Locked = false, bool DefaultVisible = true);

/// <summary>
/// Bir liste ekranının sütun ve sıralama durumu (ADR-0036): hangi sütunlar, hangi sırada, hangi sıralama. Kişisel görünüm (<see cref="GridViewDto"/>)
/// bu durumun kaydedilmiş halidir; <see cref="Apply"/> görünümü uygular, <see cref="Differs"/> kaydedilmemiş değişikliği söyler.
/// Ekranda artık bulunmayan sütun anahtarı yok sayılır; tanımda yeni eklenen sütun gizli gelir.
/// </summary>
public sealed class GridViewState(string screenKey, string defaultSort)
{
    private IReadOnlyList<GridColumnDef> definitions = [];

    public string ScreenKey => screenKey;

    public string DefaultSort => defaultSort;

    /// <summary>Bütün sütunların sırası (görünenler ve gizliler).</summary>
    public List<string> Order { get; } = [];

    public HashSet<string> Visible { get; } = [];

    /// <summary>Geçerli sıralama anahtarı (ör. <c>-name</c>).</summary>
    public string Sort { get; set; } = defaultSort;

    public IReadOnlyList<GridColumnDef> Definitions => definitions;

    /// <summary>Görünen sütunlar, sırasıyla.</summary>
    public IEnumerable<GridColumnDef> VisibleColumns =>
        Order.Where(Visible.Contains).Select(Find).OfType<GridColumnDef>();

    /// <summary>Görünen sütun anahtarları, sırasıyla (kişisel görünümün <c>Columns</c> değeri).</summary>
    public List<string> VisibleKeys => [.. Order.Where(Visible.Contains)];

    public GridColumnDef? Find(string key) => definitions.FirstOrDefault(d => d.Key == key);

    /// <summary>Sütun tanımlarını kurar ve varsayılan duruma döner.</summary>
    public void Define(IReadOnlyList<GridColumnDef> columns)
    {
        definitions = columns;
        Reset();
    }

    public void Reset()
    {
        Order.Clear();
        Order.AddRange(definitions.Select(d => d.Key));
        Visible.Clear();
        foreach (var column in definitions.Where(d => d.DefaultVisible || d.Locked))
        {
            Visible.Add(column.Key);
        }

        Sort = defaultSort;
    }

    /// <summary>Kayıtlı görünümü uygular: kayıtlı sütunlar sırayla görünür, tanımdaki diğerleri gizli kalır, sabit sütun hep görünür.</summary>
    public void Apply(GridViewDto view)
    {
        var known = KnownColumns(view);
        Order.Clear();
        Order.AddRange(known);
        Order.AddRange(definitions.Select(d => d.Key).Where(k => !known.Contains(k)));
        Visible.Clear();
        foreach (var key in known)
        {
            Visible.Add(key);
        }

        Sort = string.IsNullOrEmpty(view.Sort) ? defaultSort : view.Sort;
    }

    /// <summary>Durum, karşılaştırılan görünümden (null = varsayılan) farklı mı? Yalnızca görünen sütunların sırası ve sıralama sayılır.</summary>
    public bool Differs(GridViewDto? baseline)
    {
        if (baseline is null)
        {
            var defaults = definitions.Where(d => d.DefaultVisible || d.Locked).Select(d => d.Key);
            return !defaults.SequenceEqual(VisibleKeys) || Sort != defaultSort;
        }

        var expectedSort = string.IsNullOrEmpty(baseline.Sort) ? defaultSort : baseline.Sort;
        return !KnownColumns(baseline).SequenceEqual(VisibleKeys) || Sort != expectedSort;
    }

    /// <summary>Durumu kişisel görünüm isteğine çevirir (sıralama varsayılansa kaydedilmez).</summary>
    public SaveGridViewRequest ToRequest(string name, string? filters) =>
        new(screenKey, name, VisibleKeys, Sort == defaultSort ? null : Sort, filters);

    public void Toggle(string key)
    {
        if (Find(key) is not { Locked: false })
        {
            return;
        }

        if (!Visible.Remove(key))
        {
            Visible.Add(key);
        }
    }

    public void Move(string key, int delta)
    {
        var index = Order.IndexOf(key);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Order.Count)
        {
            return;
        }

        Order.RemoveAt(index);
        Order.Insert(target, key);
    }

    // Görünümün ekranda hâlâ var olan sütunları (tekrarsız); sabit sütun yoksa başa eklenir.
    private List<string> KnownColumns(GridViewDto view)
    {
        var known = view.Columns.Where(c => Find(c) is not null).Distinct().ToList();
        foreach (var locked in definitions.Where(d => d.Locked && !known.Contains(d.Key)).Reverse())
        {
            known.Insert(0, locked.Key);
        }

        return known;
    }
}
