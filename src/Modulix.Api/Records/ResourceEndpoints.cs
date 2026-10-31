using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Core.Contracts.Records;
using Modulix.Shared.Records;

namespace Modulix.Api.Records;

/// <summary>
/// Bir modül kaynağının tipli CRUD uç noktaları; her biri tek bir kayıt boru hattı çağrısıdır. Tenant, modül hakkı, izin, doğrulama,
/// audit ve olay boru hattında uygulanır. Modül tenant için kapalıysa veya izin yoksa 403; başka tenant'ın kaydı 404 döner.
/// CRM ve sözleşme modülü aynı kalıbı kullanır; yeni bir modül tek satırda kaynak ekler.
/// </summary>
public static class ResourceEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <param name="apiPrefix">Oluşturma yanıtındaki konum için grup yolu (ör. "/api/crm").</param>
    /// <param name="createPermission">Oluşturma izni; verilmezse yazma izni.</param>
    /// <param name="deletePermission">Silme izni; verilmezse yazma izni.</param>
    public static void MapResource<TDto, TSave>(
        RouteGroupBuilder group, string apiPrefix, string path, string entityKey, string readPermission, string writePermission,
        string? createPermission = null, string? deletePermission = null)
        where TSave : class
    {
        var resource = new Resource<TDto, TSave>($"{apiPrefix}/{path}", entityKey);
        var routes = group.MapGroup(path);

        routes.MapGet("", resource.ListAsync).RequirePermission(readPermission);
        routes.MapGet("/{id:guid}", resource.GetAsync).RequirePermission(readPermission);
        // Gövde elle okunur (Accepts ile belgelenir): genel tipli parametre ASP.NET route analizörünü çökertiyor.
        routes.MapPost("", resource.CreateAsync).Accepts<TSave>("application/json").RequirePermission(createPermission ?? writePermission);
        routes.MapPut("/{id:guid}", resource.UpdateAsync).Accepts<TSave>("application/json").RequirePermission(writePermission);
        routes.MapDelete("/{id:guid}", resource.DeleteAsync).RequirePermission(deletePermission ?? writePermission);
    }

    private sealed class Resource<TDto, TSave>(string location, string entityKey)
        where TSave : class
    {
        public async Task<IResult> ListAsync(int? page, int? pageSize, string? search, HttpRequest http, IRecordPipeline pipeline, CancellationToken ct)
        {
            var query = new RecordQuery(entityKey, page ?? 1, pageSize ?? 50, search, RecordEndpoints.FiltersFrom(http));
            var result = await pipeline.QueryAsync(query, ct);
            return result.IsSuccess
                ? Results.Ok(new PagedResult<TDto>(result.Value!.Items.Select(ToDto<TDto>).ToList(), result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
                : RecordEndpoints.ToProblem(result.Error!);
        }

        public async Task<IResult> GetAsync(Guid id, IRecordPipeline pipeline, CancellationToken ct)
        {
            var result = await pipeline.GetAsync(new RecordRef(entityKey, id), ct);
            return result.IsSuccess ? Results.Ok(ToDto<TDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }

        public async Task<IResult> CreateAsync(HttpRequest http, IRecordPipeline pipeline, CancellationToken ct)
        {
            if (await ReadAsync(http, ct) is not { } request)
            {
                return InvalidBody();
            }

            var (values, _) = ToValues(request);
            var result = await pipeline.CreateAsync(new RecordCommand(entityKey, null, values), ct);
            return result.IsSuccess ? Results.Created($"{location}/{result.Value!.Id}", ToDto<TDto>(result.Value)) : RecordEndpoints.ToProblem(result.Error!);
        }

        public async Task<IResult> UpdateAsync(Guid id, HttpRequest http, IRecordPipeline pipeline, CancellationToken ct)
        {
            if (await ReadAsync(http, ct) is not { } request)
            {
                return InvalidBody();
            }

            var (values, version) = ToValues(request);
            if (version is null)
            {
                return RecordEndpoints.VersionRequired();
            }

            var result = await pipeline.UpdateAsync(new RecordCommand(entityKey, id, values, version), ct);
            return result.IsSuccess ? Results.Ok(ToDto<TDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }

        public async Task<IResult> DeleteAsync(Guid id, long? version, IRecordPipeline pipeline, CancellationToken ct)
        {
            if (version is null)
            {
                return RecordEndpoints.VersionRequired();
            }

            var result = await pipeline.DeleteAsync(new RecordRef(entityKey, id), version, ct);
            return result.IsSuccess ? Results.NoContent() : RecordEndpoints.ToProblem(result.Error!);
        }

        private static async Task<TSave?> ReadAsync(HttpRequest http, CancellationToken ct)
        {
            if (!http.HasJsonContentType())
            {
                return null;
            }

            try
            {
                return await http.ReadFromJsonAsync<TSave>(Json, ct);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IResult InvalidBody() =>
            Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek.", detail: "Gövde JSON olmalıdır.");
    }

    /// <summary>İstek gövdesi → kayıt alanları. Tipli istek her alanı gönderir: PUT kaydın tamamını yazar.</summary>
    public static (JsonObject Values, long? Version) ToValues<TSave>(TSave request)
    {
        var values = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(request, Json))!.AsObject();
        var version = values["version"]?.GetValue<long>();
        values.Remove("version");
        return (values, version);
    }

    public static TDto ToDto<TDto>(RecordData data)
    {
        var json = data.Values.DeepClone().AsObject();
        json["id"] = data.Id;
        json["version"] = data.Version;
        return json.Deserialize<TDto>(Json)!;
    }
}
