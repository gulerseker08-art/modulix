using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Kısa süreli bildirim kuyruğu: sıra, kapasite, kapatma ve değişiklik bildirimi.</summary>
public class ToastServiceTests
{
    [Fact]
    public void Added_toasts_keep_their_order_and_kind()
    {
        ToastService toasts = new();

        toasts.Add("Kaydedildi.", ToastKind.Success);
        toasts.Add("Sunucuya ulaşılamadı.", ToastKind.Error);

        Assert.Equal(["Kaydedildi.", "Sunucuya ulaşılamadı."], toasts.Items.Select(t => t.Text));
        Assert.Equal([ToastKind.Success, ToastKind.Error], toasts.Items.Select(t => t.Kind));
        Assert.Equal(2, toasts.Items.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Oldest_toast_is_dropped_when_capacity_is_exceeded()
    {
        ToastService toasts = new();

        for (int i = 0; i <= ToastService.Capacity; i++)
        {
            toasts.Add($"Bildirim {i}", ToastKind.Info);
        }

        Assert.Equal(ToastService.Capacity, toasts.Items.Count);
        Assert.Equal("Bildirim 1", toasts.Items[0].Text);
    }

    [Fact]
    public void Dismiss_removes_only_the_given_toast_and_ignores_unknown_ids()
    {
        ToastService toasts = new();
        ToastItem first = toasts.Add("Bir", ToastKind.Success);
        ToastItem second = toasts.Add("İki", ToastKind.Success);
        int changes = 0;
        toasts.Changed += () => changes++;

        toasts.Dismiss(first.Id);
        toasts.Dismiss(first.Id);
        toasts.Dismiss(9999);

        Assert.Equal([second], toasts.Items);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Show_notifies_listeners_and_returns_the_visible_toast()
    {
        ToastService toasts = new();
        int changes = 0;
        toasts.Changed += () => changes++;

        ToastItem shown = toasts.Show("Silindi.");

        Assert.Equal(1, changes);
        Assert.Equal(ToastKind.Success, shown.Kind);
        Assert.Contains(shown, toasts.Items);
    }
}
