using Microsoft.AspNetCore.Http.Features;

namespace Modulix.Api.Imports;

/// <summary>Gövdesi dosya olan istekler: sınır sunucuda uygulanır, istemci başlığına güvenilmez; aşılırsa 413 döner.</summary>
public static class LimitedBody
{
    public static async Task<(byte[]? Content, IResult? Problem)> ReadAsync(HttpRequest http, int limit, CancellationToken ct)
    {
        var tooLarge = Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"Dosya en fazla {limit / (1024 * 1024)} MB olabilir.");
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = limit + 1024;
        }

        if (http.ContentLength > limit)
        {
            return (null, tooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (null, tooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), null);
    }
}
