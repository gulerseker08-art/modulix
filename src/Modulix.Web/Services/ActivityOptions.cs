using Modulix.Shared.Sectors;

namespace Modulix.Web.Services;

/// <summary>
/// Faaliyet seçenekleri (ADR-0029): kayıt formlarındaki seçiciler ve kod → ad gösterimi için aktif faaliyet ağacı. Oturum boyunca bir kez
/// yüklenir; yükleme başarısız olursa önbelleğe alınmaz ve sonraki çağrıda yeniden denenir.
/// </summary>
public sealed class ActivityOptions(ApiClient api)
{
    private IReadOnlyList<ActivityOptionDto>? items;

    public async Task<IReadOnlyList<ActivityOptionDto>> LoadAsync()
    {
        if (items is null)
        {
            var result = await api.GetAsync<List<ActivityOptionDto>>("/api/activity-options");
            if (!result.IsSuccess || result.Value is null)
            {
                return [];
            }

            items = result.Value;
        }

        return items;
    }

    /// <summary>Kodun adı; katalogda yoksa (ör. sonradan pasife alınmış) kodun kendisi.</summary>
    public string NameOf(string code) => items?.FirstOrDefault(a => a.Code == code)?.Name ?? code;
}
