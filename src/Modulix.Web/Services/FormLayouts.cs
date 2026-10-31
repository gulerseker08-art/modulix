using Modulix.Shared.Customization;

namespace Modulix.Web.Services;

/// <summary>
/// Formların etkin düzenleri (ADR-0021): <c>GET /api/form-layouts</c> oturum başına bir kez okunur, tenant değişince
/// yeniden. Editörler bölüm / sekme / alan yerleşimini buradan alır; yönetici form tasarımında kaydedince
/// <see cref="Invalidate"/> ile tazelenir.
/// </summary>
public sealed class FormLayouts(ApiClient api, SessionState session)
{
    private IReadOnlyList<FormLayoutDto>? layouts;
    private Guid? loadedFor;

    public async Task<IReadOnlyList<FormLayoutDto>> AllAsync()
    {
        Guid? tenant = session.User?.ActiveTenantId;
        if (layouts is null || loadedFor != tenant)
        {
            var result = await api.GetAsync<List<FormLayoutDto>>("/api/form-layouts");
            if (!result.IsSuccess)
            {
                return layouts ?? [];
            }

            layouts = result.Value ?? [];
            loadedFor = tenant;
        }

        return layouts;
    }

    /// <summary>Kayıt türünün düzeni; yüklenemediyse ya da tanımsızsa null.</summary>
    public async Task<FormLayoutDto?> ForAsync(string entityKey) =>
        (await AllAsync()).FirstOrDefault(l => l.EntityKey == entityKey);

    public void Invalidate() => layouts = null;
}

public static class FormLayoutText
{
    /// <summary>Etiket sınıfı: zorunlu alanda kırmızı yıldız (CSS <c>.form-label.required</c>).</summary>
    public static string LabelClass(this FormFieldDto field) => field.Required ? "form-label required" : "form-label";

    public static string? AriaRequired(this FormFieldDto field) => field.Required ? "true" : null;
}
