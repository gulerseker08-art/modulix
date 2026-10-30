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

    /// <summary>İstek gövdesi JSON, yanıtı dosya olan çağrı (ör. oturumsuz müşteri sayfasının PDF'i; belirteç gövdede gider).</summary>
    public Task<ApiResult<byte[]>> PostForFileAsync(string url, object body) => SendAsync<byte[]>(() => http.PostAsJsonAsync(url, body, Json));

    /// <summary>Gövdesi dosyanın kendisi olan istek (ör. logo).</summary>
    public Task<ApiResult<T>> PutBytesAsync<T>(string url, byte[] body, string contentType) => SendAsync<T>(() =>
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return http.PutAsync(url, content);
    });

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

                if (typeof(T) == typeof(byte[]))
                {
                    return ApiResult<T>.Ok((T)(object)await response.Content.ReadAsByteArrayAsync());
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
                HttpStatusCode.UnprocessableEntity => problem?.Message ?? "Lütfen işaretli alanları düzeltin.",
                HttpStatusCode.TooManyRequests => "Çok fazla istek gönderdiniz. Lütfen biraz bekleyip tekrar deneyin.",
                HttpStatusCode.BadRequest when problem?.Errors is { Count: > 0 } errors => errors.Values.SelectMany(e => e).First(),
                _ => problem?.Title ?? "Beklenmeyen bir hata oluştu.",
            };

            return ApiResult<T>.Fail(response.StatusCode, message, problem?.Errors, problem?.Code);
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

    private sealed record ApiProblem(string? Title, string? Detail, Dictionary<string, string[]>? Errors, string? Code = null, string? Message = null);
}

public sealed class ApiResult<T>
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    private ApiResult(T? value, HttpStatusCode status, string? error, IReadOnlyDictionary<string, string[]>? fieldErrors, string? code = null)
    {
        Code = code;
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

    /// <summary>Sunucunun kararlı hata anahtarı (ör. price-not-found); yoksa null.</summary>
    public string? Code { get; }

    public static ApiResult<T> Ok(T value) => new(value, HttpStatusCode.OK, null, null);

    public static ApiResult<T> Fail(HttpStatusCode status, string error, IReadOnlyDictionary<string, string[]>? fieldErrors = null, string? code = null) =>
        new(default, status, error, fieldErrors, code);
}
