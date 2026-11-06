using Modulix.Shared.Views;
using Modulix.Web.Components;

namespace Modulix.Web.Services;

/// <summary>
/// Bir liste ekranının kişisel görünümleri (ADR-0036): yükleme, kaydetme, güncelleme, silme ve sekme seçenekleri. Sayfa başına bir örnek
/// (<c>new PersonalGridViews(Api, "crm.companies")</c>); sütun/sıralama durumu <see cref="GridViewState"/>'te, süzgeç sayfadadır.
/// Sunucu hatası <see cref="Error"/>'a yazılır, <see cref="PersonalViewBar"/> gösterir.
/// </summary>
public sealed class PersonalGridViews(ApiClient api, string screenKey)
{
    /// <summary>Kişisel görünüm sekmelerinin anahtar öneki (<c>view:{id}</c>).</summary>
    public const string Prefix = "view:";

    private const string Url = "/api/grid-views";

    public string ScreenKey => screenKey;

    public List<GridViewDto> Items { get; private set; } = [];

    /// <summary>Yeni görünüm adı girişi açık mı.</summary>
    public bool Naming { get; set; }

    public string? Error { get; private set; }

    public async Task LoadAsync()
    {
        var result = await api.GetAsync<List<GridViewDto>>($"{Url}?screen={Uri.EscapeDataString(screenKey)}");
        Items = result.Value ?? [];
    }

    /// <summary>Görünüm sekmesinin anahtarından kişisel görünüm; sistem görünümü anahtarında null.</summary>
    public GridViewDto? Find(string viewKey) => viewKey.StartsWith(Prefix, StringComparison.Ordinal)
        ? Items.FirstOrDefault(v => Prefix + v.Id == viewKey)
        : null;

    /// <summary>Sekme seçenekleri (kullanıcı simgeli); sistem görünümlerinin ardına eklenir.</summary>
    public IEnumerable<ViewOption> Options => Items.Select(v => new ViewOption(Prefix + v.Id, v.Name, Icon: "user"));

    public void StartNaming(bool value)
    {
        Naming = value;
        Error = null;
    }

    /// <summary>Yeni görünüm olarak kaydeder; başarılıysa yeni görünümün sekme anahtarı, değilse null (hata <see cref="Error"/>'da).</summary>
    public async Task<string?> SaveAsAsync(SaveGridViewRequest request)
    {
        var result = await api.PostAsync<GridViewDto>(Url, request);
        if (result.Value is not { } created)
        {
            Error = result.Error;
            return null;
        }

        Items = [.. Items.Append(created).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)];
        Naming = false;
        Error = null;
        return Prefix + created.Id;
    }

    public async Task UpdateAsync(GridViewDto current, SaveGridViewRequest request)
    {
        var result = await api.PutAsync<GridViewDto>($"{Url}/{current.Id}", request with { Version = current.Version });
        if (result.Value is not { } updated)
        {
            Error = result.Error;
            return;
        }

        Items[Items.FindIndex(v => v.Id == updated.Id)] = updated;
        Error = null;
    }

    public async Task<bool> DeleteAsync(GridViewDto current)
    {
        var result = await api.DeleteAsync($"{Url}/{current.Id}?version={current.Version}");
        if (!result.IsSuccess)
        {
            Error = result.Error;
            return false;
        }

        Items.Remove(current);
        Error = null;
        return true;
    }

    public void ClearError() => Error = null;
}
