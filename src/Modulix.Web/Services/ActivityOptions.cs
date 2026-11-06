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

    /// <summary>Aynı ad farklı sektörlerde tekrarlanabilir (ör. "Diğer"): üst düğümlerle birlikte "A › B" yolu.</summary>
    public string PathOf(ActivityOptionDto option)
    {
        var names = new List<string> { option.Name };
        for (var parent = option.ParentCode; parent is not null && names.Count < 6;)
        {
            var node = items?.FirstOrDefault(a => a.Code == parent);
            if (node is null) { break; }
            names.Insert(0, node.Name);
            parent = node.ParentCode;
        }

        return string.Join(" › ", names);
    }

    /// <summary>Kodun adı; katalogda yoksa (ör. sonradan pasife alınmış) kodun kendisi.</summary>
    public string NameOf(string code) => items?.FirstOrDefault(a => a.Code == code)?.Name ?? code;
}
