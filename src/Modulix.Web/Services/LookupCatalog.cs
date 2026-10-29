using Modulix.Shared.Lookups;

namespace Modulix.Web.Services;

/// <summary>
/// Lookup alanlarının firma için geçerli davranışı (ADR-0016): aranabilir mi, en az kaç karakter, bağımlılık ipucu. Oturum başına bir
/// kez okunur; yönetici ayarı değiştirdiğinde <see cref="Invalidate"/> çağrılır. Okunamazsa güvenli varsayılanla (arama açık) devam edilir.
/// </summary>
public sealed class LookupCatalog(ApiClient api)
{
    private Dictionary<string, LookupDto>? items;
    private Task? loading;

    public async Task<LookupDto?> GetAsync(string key)
    {
        await (loading ??= LoadAsync());
        return items!.GetValueOrDefault(key);
    }

    /// <summary>Bir sonraki okumada ayarlar yeniden alınır.</summary>
    public void Invalidate()
    {
        items = null;
        loading = null;
    }

    private async Task LoadAsync()
    {
        var result = await api.GetAsync<List<LookupDto>>("/api/lookups");
        if (!result.IsSuccess)
        {
            // Başarısız okuma kalıcılaşmasın: sonraki bileşen yeniden dener.
            items = [];
            loading = null;
            return;
        }

        items = (result.Value ?? []).ToDictionary(l => l.Key, StringComparer.Ordinal);
    }
}
