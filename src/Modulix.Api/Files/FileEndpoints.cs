using Microsoft.AspNetCore.Mvc;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Files;
using Modulix.Core.Contracts.Records;
using Modulix.Shared.Files;

namespace Modulix.Api.Files;

/// <summary>
/// Kayda bağlı dosyalar (ADR-0040): <c>/api/files</c>. Ek izin istemez; yetki bağlı kaydın erişiminden gelir ve her istekte sunucuda uygulanır
/// (<see cref="IFileStore"/>). Tenant yalnızca oturumdan gelir. İndirme her zaman <c>attachment</c> olarak sunulur; içerik tarayıcıda çalıştırılmaz.
/// </summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Files");

        group.MapGet("/", async (string entityKey, Guid recordId, IFileStore files, CancellationToken ct) =>
        {
            var result = await files.ListAsync(new RecordRef(entityKey, recordId), ct);
            return result.IsSuccess ? Results.Ok(result.Value!.Select(ToDto).ToList()) : RecordEndpoints.ToProblem(result.Error!);
        });

        // Çok parçalı gövde: entityKey, recordId, file. Sınır gövde okunmadan Content-Length ile, sonra okuma sırasında yeniden denetlenir.
        group.MapPost("/", async (HttpRequest request, IFileStore files, IFileLimits limits, CancellationToken ct) =>
        {
            if (request.ContentLength is { } length && length > limits.MaxFileBytes + 64 * 1024)
            {
                return TooLarge(limits.MaxFileBytes);
            }

            if (!request.HasFormContentType)
            {
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "İstek multipart/form-data olmalıdır.");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (!Guid.TryParse(form["recordId"], out var recordId) || string.IsNullOrWhiteSpace(form["entityKey"]) || file is null)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["file"] = ["entityKey, recordId ve file alanları zorunludur."] },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (file.Length > limits.MaxFileBytes)
            {
                return TooLarge(limits.MaxFileBytes);
            }

            await using var stream = file.OpenReadStream();
            var result = await files.AddAsync(new RecordRef(form["entityKey"].ToString(), recordId), file.FileName, stream, ct);
            return result.IsSuccess
                ? Results.Created($"/api/files/{result.Value!.Id}/content", ToDto(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).DisableAntiforgery();

        group.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IFileStore files, CancellationToken ct) =>
        {
            var result = await files.OpenAsync(id, ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";

            // Results.File akışı yanıt bitince kapatır ve Content-Disposition'ı attachment yazar.
            return Results.File(result.Value!.Content, result.Value.Info.ContentType, result.Value.Info.FileName);
        });

        group.MapDelete("/{id:guid}", async (Guid id, [FromQuery] long version, IFileStore files, CancellationToken ct) =>
        {
            var result = await files.DeleteAsync(id, version, ct);
            return result.IsSuccess ? Results.NoContent() : RecordEndpoints.ToProblem(result.Error!);
        });

        return app;
    }

    private static IResult TooLarge(long max) =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Dosya çok büyük.", detail: $"En fazla {max / (1024 * 1024)} MB.");

    private static StoredFileDto ToDto(StoredFileInfo f) =>
        new(f.Id, f.Version, f.EntityKey, f.RecordId, f.FileName, f.ContentType, f.SizeBytes, f.Sha256, f.ScanStatus, f.UploadedByUserId, f.CreatedAt);
}
