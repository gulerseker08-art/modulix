using Modulix.Api.Access;
using Modulix.Api.Users;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Shared.Access;

namespace Modulix.Api.Organization;

/// <summary>
/// İşletici şirketler ve kullanıcıların şirket görevleri (ADR-0008, adım 2). Yapılandırma uçlarıdır: hepsi aktif tenant
/// ve <c>core.companies.manage</c> ister. Aktif şirket seçimi ayrı bir uçtadır (<c>/api/auth/active-company</c>) ve her
/// üyeye açıktır; bu uçlar yönetim içindir.
/// </summary>
public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var companies = app.MapGroup("/api/companies")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageCompanies)
            .WithTags("Companies");

        companies.MapGet("/", ListAsync);
        companies.MapPost("/", CreateAsync);
        companies.MapPut("/{companyId:guid}", RenameAsync);

        // Bir üyeliğin şirket görevleri: şirket yönetimine (core.companies.manage) bağlıdır.
        var memberships = app.MapGroup("/api/memberships/{membershipId:guid}/companies")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageCompanies)
            .WithTags("Companies");

        memberships.MapGet("/", GetMembershipCompaniesAsync);
        memberships.MapPut("/", SetMembershipCompaniesAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(ICompanyAdministration companies, CancellationToken ct) =>
        Results.Ok((await companies.ListAsync(ct)).Select(c => new OperatingCompanyDto(c.Id, c.Name, c.IsDefault)).ToList());

    private static async Task<IResult> CreateAsync(SaveOperatingCompanyRequest request, ICompanyAdministration companies, CancellationToken ct)
    {
        try
        {
            var company = await companies.CreateAsync(request.Name, ct);
            return Results.Created($"/api/companies/{company.Id}", new OperatingCompanyDto(company.Id, company.Name, company.IsDefault));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> RenameAsync(Guid companyId, SaveOperatingCompanyRequest request, ICompanyAdministration companies, CancellationToken ct)
    {
        try
        {
            var company = await companies.RenameAsync(companyId, request.Name, ct);
            return company is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Şirket bulunamadı.")
                : Results.Ok(new OperatingCompanyDto(company.Id, company.Name, company.IsDefault));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> GetMembershipCompaniesAsync(Guid membershipId, ICompanyAdministration companies, CancellationToken ct) =>
        await companies.GetMembershipCompaniesAsync(membershipId, ct) is { } ids
            ? Results.Ok(ids)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");

    private static async Task<IResult> SetMembershipCompaniesAsync(
        Guid membershipId, SetMembershipCompaniesRequest request, ICompanyAdministration companies, CancellationToken ct)
    {
        try
        {
            return await companies.SetMembershipCompaniesAsync(membershipId, request.CompanyIds ?? [], ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }
}
