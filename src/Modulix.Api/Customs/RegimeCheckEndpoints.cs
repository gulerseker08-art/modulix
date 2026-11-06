using Modulix.Api.Access;
using Modulix.Core.Contracts.Customs;
using Modulix.Modules.Warehouse;
using Modulix.Shared.Customs;
using Modulix.Shared.Warehouse;

namespace Modulix.Api.Customs;

/// <summary>
/// Rejim profili: tenant tarafı (ADR-0032 K16). Profil global ve platformca yönetilir; tenant yalnızca yayındaki etkin profilleri görür
/// (<c>GET /api/warehouse/regime/profiles</c>, bağlama okuma izni) ve beyannamenin tescil numarasını rejiminin profiline göre denetletir
/// (<c>GET /api/warehouse/regime/check</c>, beyanname okuma izni). Denetim yalnızca bilgidir; engel düzeyi kayıt boru hattında uygulanır.
/// </summary>
public static class RegimeCheckEndpoints
{
    public static IEndpointRouteBuilder MapRegimeCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/warehouse/regime")
            .RequireAuthorization()
            .RequireModule(WarehouseAccess.ModuleKey)
            .WithTags("Warehouse");

        group.MapGet("/profiles", async (IRegimeProfileCatalog catalog, CancellationToken ct) =>
                (await catalog.ListPublishedAsync(ct)).Select(p => new PublishedRegimeProfileDto(
                    p.ProfileId, p.Code, p.Name, p.VersionNumber, p.IsVerified, p.VerificationNote,
                    p.Content.RegistrationNo is { } r ? new RegistrationNoRuleDto(r.Pattern, r.MinLength, r.MaxLength, r.Example, r.Enforcement.ToString()) : null)).ToList())
            .RequirePermission(WarehouseAccess.RegimesRead);

        group.MapGet("/check", async (string? regimeKey, string? registrationNo, IRegistrationNoCheck check, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(regimeKey) || string.IsNullOrWhiteSpace(registrationNo))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Rejim ve tescil numarası gerekli.");
                }

                var result = await check.CheckAsync(regimeKey.Trim(), registrationNo.Trim(), ct);
                return Results.Ok(new RegistrationNoCheckDto(result.HasRule, result.Enforcement, [.. result.Violations], result.ProfileName, result.VersionNumber, result.Verified));
            })
            .RequirePermission(WarehouseAccess.DeclarationsRead);

        return app;
    }
}
