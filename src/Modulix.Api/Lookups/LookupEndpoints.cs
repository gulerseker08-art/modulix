using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Lookups;
using Modulix.Core.Modules;
using Modulix.Shared.Lookups;

namespace Modulix.Api.Lookups;

/// <summary>
/// Lookup uç noktaları (ADR-0016). Okuma, arama ve çözümleme her üyeye açıktır; hedef kayıtlara erişim kayıt boru hattının
/// izin ve veri kapsamı kurallarıyla belirlenir ("aranabilir" bir erişim yetkisi değildir). Ayar yazma
/// <c>core.customization.manage</c> ister. Tenant yalnızca oturumdan gelir.
/// </summary>
public static class LookupEndpoints
{
    public static IEndpointRouteBuilder MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var lookups = app.MapGroup("/api/lookups")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Lookups");

        lookups.MapGet("/", async (ILookupSettings settings, ModuleCatalog catalog, CancellationToken ct) =>
            (await settings.ListAsync(ct)).Select(i => ToDto(i, catalog)).ToList());

        lookups.MapGet("/{key}/search", async (
            string key, string? q, int? page, int? pageSize, Guid? parentId, ILookupSearch search, CancellationToken ct) =>
        {
            var result = await search.SearchAsync(new LookupSearch(key, q, page ?? 1, pageSize ?? 0, parentId), ct);
            return result.IsSuccess
                ? Results.Ok(new LookupPageDto(
                    [.. result.Value!.Items.Select(ToDto)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount,
                    result.Value.Searchable, result.Value.CanView))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        lookups.MapPost("/{key}/resolve", async (
            string key, ResolveLookupRequest request, ILookupSearch search, ILookupSettings settings, IRecordAccess access, CancellationToken ct) =>
        {
            var result = await search.ResolveAsync(key, request.Ids ?? [], ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var target = (await settings.FindAsync(key, ct))?.Definition.TargetEntityKey;
            var canView = target is not null && await access.CanAsync(target, RecordOperation.Read, ct);
            return Results.Ok(new LookupResolveDto([.. result.Value!.Select(ToDto)], canView));
        });

        app.MapGroup("/api/customization/lookups")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization")
            .MapPut("/{key}", async (string key, SaveLookupSettingRequest request, ILookupSettings settings, ModuleCatalog catalog, CancellationToken ct) =>
            {
                var result = await settings.SaveAsync(key, request.Searchable, request.Version, ct);
                return result.IsSuccess ? Results.Ok(ToDto(result.Value!, catalog)) : RecordEndpoints.ToProblem(result.Error!);
            });

        return app;
    }

    private static string? NameOf(ModuleCatalog catalog, string entityKey) => catalog.FindAuditedEntity(entityKey)?.DisplayName;

    private static LookupItemDto ToDto(LookupItem item) => new(item.Id, item.Label, item.Secondary, item.Unavailable);

    internal static LookupDto ToDto(LookupInfo info, ModuleCatalog catalog) => new(
        info.Definition.Key,
        info.ModuleKey,
        info.Definition.SourceEntityKey,
        NameOf(catalog, info.Definition.SourceEntityKey) ?? info.Definition.SourceEntityKey,
        info.Definition.Field,
        info.Definition.Title,
        info.Definition.IsMember,
        info.Definition.TargetEntityKey,
        info.Definition.TargetEntityKey is null ? null : NameOf(catalog, info.Definition.TargetEntityKey),
        info.Definition.SearchDescription,
        info.Definition.DefaultSearchable,
        info.Searchable,
        info.Customized,
        info.Version,
        info.Definition.MinSearchLength,
        info.Definition.PageSize,
        info.Definition.DependsOnField,
        info.Definition.DependsOnHint,
        info.Definition.ViewPath);
}
