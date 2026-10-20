using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Modulix.Web.Services;

/// <summary>
/// API ile konuşmanın tek yolu. Oturum çerezi tarayıcı tarafından eklenir (aynı adres). Hatalar istisna
/// değil sonuç olarak döner: ekran alan hatalarını alanın altında, diğerlerini sayfanın üstünde gösterir.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(() => http.GetAsync(url));

    public Task<ApiResult<T>> PostAsync<T>(string url, object body) => SendAsync<T>(() => http.PostAsJsonAsync(url, body, Json));

    public Task<ApiResult<T>> PutAsync<T>(string url, object body) => SendAsync<T>(() => http.PutAsJsonAsync(url, body, Json));

    public Task<ApiResult<bool>> DeleteAsync(string url) => SendAsync<bool>(() => http.DeleteAsync(url));

    private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Fail(0, "Sunucuya ulaşılamadı. Bağlantınızı kontrol edip tekrar deneyin.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(bool))
                {
                    return ApiResult<T>.Ok(typeof(T) == typeof(bool) ? (T)(object)true : default!);
                }

                return ApiResult<T>.Ok((await response.Content.ReadFromJsonAsync<T>(Json))!);
            }

            var problem = await ReadProblemAsync(response);
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Oturumunuz kapanmış. Lütfen yeniden giriş yapın.",
                HttpStatusCode.Forbidden => problem?.Detail ?? problem?.Title ?? "Bu işlem için yetkiniz yok.",
                HttpStatusCode.NotFound => problem?.Title ?? "Kayıt bulunamadı.",
                HttpStatusCode.Conflict => problem?.Title ?? "Kayıt başka biri tarafından değiştirildi.",
                HttpStatusCode.UnprocessableEntity => "Lütfen işaretli alanları düzeltin.",
                _ => problem?.Title ?? "Beklenmeyen bir hata oluştu.",
            };

            return ApiResult<T>.Fail(response.StatusCode, message, problem?.Errors);
        }
    }

    private static async Task<ApiProblem?> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiProblem>(Json);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record ApiProblem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}

public sealed class ApiResult<T>
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    private ApiResult(T? value, HttpStatusCode status, string? error, IReadOnlyDictionary<string, string[]>? fieldErrors)
    {
        Value = value;
        Status = status;
        Error = error;
        FieldErrors = fieldErrors ?? NoErrors;
    }

    public T? Value { get; }

    public HttpStatusCode Status { get; }

    /// <summary>Kullanıcıya gösterilecek mesaj; başarılıysa null.</summary>
    public string? Error { get; }

    /// <summary>Alan adı (camelCase) → mesajlar. Sunucunun doğrulama sonucudur.</summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    public bool IsSuccess => Error is null;

    public static ApiResult<T> Ok(T value) => new(value, HttpStatusCode.OK, null, null);

    public static ApiResult<T> Fail(HttpStatusCode status, string error, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(default, status, error, fieldErrors);
}
