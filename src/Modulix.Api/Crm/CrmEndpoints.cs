using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// CRM uç noktaları: /api/crm/{companies|contacts|opportunities|activities}. Tipli istekleri kayıt boru
/// hattına çevirir; tenant, modül hakkı, izin, doğrulama, audit ve olay boru hattında uygulanır.
/// Modül tenant için kapalıysa veya izin yoksa 403; başka tenant'ın kaydı 404 döner.
/// Yeni bir sektör modülü aynı kalıpla tek satırda kaynak ekler (<see cref="MapResource{TDto,TSave}"/>).
/// </summary>
public static class CrmEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static IEndpointRouteBuilder MapCrmEndpoints(this IEndpointRouteBuilder app)
    {
        var crm = app.MapGroup("/api/crm")
            .RequireAuthorization()
            .RequireModule(CrmModule.ModuleKey)
            .WithTags("CRM");

        MapResource<CompanyDto, SaveCompanyRequest>(crm, "companies", CrmModule.Entities.Company,
            CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite);
        MapResource<ContactDto, SaveContactRequest>(crm, "contacts", CrmModule.Entities.Contact,
            CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite);
        MapResource<OpportunityDto, SaveOpportunityRequest>(crm, "opportunities", CrmModule.Entities.Opportunity,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite);
        MapResource<ActivityDto, SaveActivityRequest>(crm, "activities", CrmModule.Entities.Activity,
            CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite);

        return app;
    }

    private static void MapResource<TDto, TSave>(
        RouteGroupBuilder crm, string path, string entityKey, string readPermission, string writePermission)
        where TSave : class
    {
        var resource = new CrmResource<TDto, TSave>(path, entityKey);
        var group = crm.MapGroup(path);

        group.MapGet("", resource.ListAsync).RequirePermission(readPermission);
        group.MapGet("/{id:guid}", resource.GetAsync).RequirePermission(readPermission);
        // Gövde elle okunur (Accepts ile belgelenir): genel tipli parametre ASP.NET route analizörünü çökertiyor.
        group.MapPost("", resource.CreateAsync).Accepts<TSave>("application/json").RequirePermission(writePermission);
        group.MapPut("/{id:guid}", resource.UpdateAsync).Accepts<TSave>("application/json").RequirePermission(writePermission);
        group.MapDelete("/{id:guid}", resource.DeleteAsync).RequirePermission(writePermission);
    }

    /// <summary>Bir CRM kaynağının uç noktaları; her biri tek bir boru hattı çağrısıdır.</summary>
    private sealed class CrmResource<TDto, TSave>(string path, string entityKey)
        where TSave : class
    {
        public async Task<IResult> ListAsync(
            int? page, int? pageSize, string? search, HttpRequest http, IRecordPipeline pipeline, CancellationToken ct)
        {
            var query = new RecordQuery(entityKey, page ?? 1, pageSize ?? 50, search, RecordEndpoints.FiltersFrom(http));
            var result = await pipeline.QueryAsync(query, ct);
            return result.IsSuccess
                ? Results.Ok(new CrmPage<TDto>(
                    result.Value!.Items.Select(ToDto<TDto>).ToList(), result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
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
            return result.IsSuccess
                ? Results.Created($"/api/crm/{path}/{result.Value!.Id}", ToDto<TDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
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
    private static (JsonObject Values, long? Version) ToValues<TSave>(TSave request)
    {
        var values = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(request, Json))!.AsObject();
        var version = values["version"]?.GetValue<long>();
        values.Remove("version");
        return (values, version);
    }

    private static TDto ToDto<TDto>(RecordData data)
    {
        var json = data.Values.DeepClone().AsObject();
        json["id"] = data.Id;
        json["version"] = data.Version;
        return json.Deserialize<TDto>(Json)!;
    }
}
