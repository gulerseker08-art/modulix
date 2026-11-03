namespace Modulix.Web.Components;

/// <summary>Sekme başlığı; <see cref="Count"/> doluysa yanında sayı, <see cref="Errors"/> sıfırdan büyükse hatalı alan işareti gösterilir.</summary>
public sealed record TabItem(string Key, string Label, int? Count = null, int Errors = 0);

/// <summary>
/// Kayıt sayfası sekmelerinin durumu (docs/design-system/form-standards.md → Sekmeler). Sekme ilk seçildiğinde çizilir (tembel yükleme);
/// sonra başka sekmeye geçilince DOM'da gizli kalır, böylece içindeki kaydedilmemiş form ve bileşen durumu kaybolmaz.
/// </summary>
public sealed class TabState(string initial)
{
    private readonly HashSet<string> visited = [initial];

    public string Active { get; private set; } = initial;

    public void Select(string key)
    {
        Active = key;
        visited.Add(key);
    }

    /// <summary>Sekme içeriği çizilsin mi (en az bir kez açıldı).</summary>
    public bool Rendered(string key) => visited.Contains(key);

    /// <summary>Çizilmiş ama etkin olmayan sekme gizlenir.</summary>
    public bool Hidden(string key) => Active != key;

    /// <summary>Kayıt denemesinden sonra: etkin sekmede hata yoksa hatalı ilk sekmeye geçer (hata gizli sekmede kalmaz).</summary>
    public void RevealFirstFailing(IReadOnlyList<TabItem> items)
    {
        var active = items.FirstOrDefault(t => t.Key == Active);
        if ((active?.Errors ?? 0) == 0 && items.FirstOrDefault(t => t.Errors > 0) is { } failing)
        {
            Select(failing.Key);
        }
    }
}
