using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Shared.Settings;

namespace Modulix.Api.Settings;

/// <summary>
/// Firma ayarları (Y6): /api/settings. Okuma her üyeye açıktır (ekranlar saat dilimine, para birimine bakar); yazma
/// <c>core.settings.manage</c> ister. Kurallar <see cref="ITenantSettings"/>'tadır.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Settings");

        settings.MapGet("/", async (ITenantSettings tenantSettings, CancellationToken ct) => ToResponse(await tenantSettings.GetAsync(ct)));
        settings.MapPut("/", SaveAsync).RequirePermission(CoreModule.Permissions.ManageSettings);

        return app;
    }

    private static async Task<IResult> SaveAsync(SaveSettingsRequest request, ITenantSettings tenantSettings, CancellationToken ct)
    {
        try
        {
            var values = (request.Values ?? new Dictionary<string, SettingWriteDto>())
                .ToDictionary(v => v.Key, v => new SettingWrite(v.Value?.Value ?? string.Empty, v.Value?.Version ?? 0), StringComparer.Ordinal);
            var result = await tenantSettings.SaveAsync(values, ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }
        catch (ArgumentException ex)
        {
            return UserEndpoints.ToProblem(ex)!;
        }
    }

    private static SettingsResponse ToResponse(IReadOnlyList<TenantSettingValue> values) =>
        new([.. values.Select(v => new SettingDto(
            v.Definition.Key,
            v.ModuleKey,
            v.Definition.Group,
            v.Definition.DisplayName,
            v.Definition.Description,
            (SettingKind)v.Definition.Type,
            v.Definition.MaxLength,
            v.Definition.Required,
            [.. v.Definition.Options.Select(o => new SettingOptionDto(o.Value, o.Label))],
            v.Definition.DefaultValue,
            v.Value,
            v.Version))]);
}
