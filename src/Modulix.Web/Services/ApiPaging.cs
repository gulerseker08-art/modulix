using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

public static class ApiPaging
{
    /// <summary>Sayfalı bir CRM listesinin tamamını okur (seçici ve ağaç görünümleri için); hata olursa o ana kadarki kayıtlar ve hata iletisi döner.</summary>
    public static async Task<(List<T> Items, string? Error)> GetAllAsync<T>(this ApiClient api, string url, int maxPages = 50)
    {
        var items = new List<T>();
        var separator = url.Contains('?') ? '&' : '?';
        for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            var result = await api.GetAsync<CrmPage<T>>($"{url}{separator}page={pageNumber}&pageSize=100");
            if (result.Value is not { } chunk)
            {
                return (items, result.Error);
            }

            items.AddRange(chunk.Items);
            if (items.Count >= chunk.TotalCount || chunk.Items.Count == 0)
            {
                break;
            }
        }

        return (items, null);
    }
}
