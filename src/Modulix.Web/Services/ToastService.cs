namespace Modulix.Web.Services;

/// <summary>Kısa süreli bildirimin tonu.</summary>
public enum ToastKind
{
    Success,
    Info,
    Error,
}

/// <summary>Ekranda duran bir bildirim.</summary>
public sealed record ToastItem(long Id, string Text, ToastKind Kind);

/// <summary>
/// Kısa süreli bildirimler (toast): "Kaydedildi" gibi işlem sonuçları sayfa düzenini kaydırmadan sağ altta görünür ve
/// kendiliğinden kapanır. Doğrulama hataları toast ile verilmez; alanın yanında ve formun üstünde kalır. <c>ToastHost</c> çizer.
/// </summary>
public sealed class ToastService
{
    /// <summary>Aynı anda en çok bu kadar bildirim durur; yenisi gelince en eskisi düşer.</summary>
    public const int Capacity = 4;

    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);

    private readonly List<ToastItem> items = [];
    private long lastId;

    public event Action? Changed;

    public IReadOnlyList<ToastItem> Items => items;

    public void Success(string text) => Show(text, ToastKind.Success);

    public void Info(string text) => Show(text, ToastKind.Info);

    public void Error(string text) => Show(text, ToastKind.Error);

    /// <summary>Bildirimi ekler ve süresi dolunca kaldırır; hata bildirimi okunabilsin diye daha uzun durur.</summary>
    public ToastItem Show(string text, ToastKind kind = ToastKind.Success)
    {
        ToastItem item = Add(text, kind);
        _ = DismissLaterAsync(item, kind == ToastKind.Error ? ErrorDuration : DefaultDuration);
        return item;
    }

    /// <summary>Bildirimi zamanlayıcı kurmadan ekler (<see cref="Show"/> bunu kullanır).</summary>
    public ToastItem Add(string text, ToastKind kind)
    {
        ToastItem item = new(++lastId, text, kind);
        items.Add(item);
        if (items.Count > Capacity)
        {
            items.RemoveAt(0);
        }

        Changed?.Invoke();
        return item;
    }

    public void Dismiss(long id)
    {
        if (items.RemoveAll(i => i.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private async Task DismissLaterAsync(ToastItem item, TimeSpan after)
    {
        await Task.Delay(after);
        Dismiss(item.Id);
    }
}
