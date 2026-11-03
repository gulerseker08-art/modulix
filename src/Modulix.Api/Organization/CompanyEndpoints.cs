using Modulix.Api.Access;
using Modulix.Api.Users;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
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

        // Şubeler: idari birim (ADR-0008), kapsam sınırı değil. Şirket yönetimine (core.companies.manage) bağlıdır.
        var branches = app.MapGroup("/api/companies/{companyId:guid}/branches")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageCompanies)
            .WithTags("Companies");

        branches.MapGet("/", ListBranchesAsync);
        branches.MapPost("/", CreateBranchAsync);
        branches.MapPut("/{branchId:guid}", RenameBranchAsync);

        // Bir üyeliğin şirket görevleri: şirket yönetimine (core.companies.manage) bağlıdır.
        var memberships = app.MapGroup("/api/memberships/{membershipId:guid}/companies")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageCompanies)
            .WithTags("Companies");

        memberships.MapGet("/", GetMembershipCompaniesAsync);
        memberships.MapPut("/", SetMembershipCompaniesAsync);

        return app;
    }

    private static async Task<IResult> ListBranchesAsync(Guid companyId, IBranchAdministration branches, CancellationToken ct) =>
        await branches.ListAsync(companyId, ct) is { } list
            ? Results.Ok(list.Select(b => new BranchDto(b.Id, b.CompanyId, b.Name)).ToList())
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Şirket bulunamadı.");

    private static async Task<IResult> CreateBranchAsync(
        Guid companyId, SaveBranchRequest request, IBranchAdministration branches, CancellationToken ct)
    {
        try
        {
            var branch = await branches.CreateAsync(companyId, request.Name, ct);
            return branch is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Şirket bulunamadı.")
                : Results.Created($"/api/companies/{companyId}/branches/{branch.Id}", new BranchDto(branch.Id, branch.CompanyId, branch.Name));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> RenameBranchAsync(
        Guid companyId, Guid branchId, SaveBranchRequest request, IBranchAdministration branches, CancellationToken ct)
    {
        try
        {
            var branch = await branches.RenameAsync(companyId, branchId, request.Name, ct);
            return branch is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Şube bulunamadı.")
                : Results.Ok(new BranchDto(branch.Id, branch.CompanyId, branch.Name));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
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

    // Kapsam dışı üyelik (başka bayinin kullanıcısı) başka tenant'ınki gibi "bulunamadı" döner (ADR-0026).
    private static async Task<IResult> GetMembershipCompaniesAsync(
        Guid membershipId, ICompanyAdministration companies, IMembershipScope scope, CancellationToken ct) =>
        await scope.CanManageAsync(membershipId, ct) && await companies.GetMembershipCompaniesAsync(membershipId, ct) is { } assignments
            ? Results.Ok(assignments.Select(a => new CompanyAssignment(a.CompanyId, a.ValidFrom, a.ValidUntil, a.BranchId)).ToList())
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");

    private static async Task<IResult> SetMembershipCompaniesAsync(
        Guid membershipId, SetMembershipCompaniesRequest request, ICompanyAdministration companies, IMembershipScope scope, CancellationToken ct)
    {
        try
        {
            var assignments = (request.Assignments ?? [])
                .Select(a => new MembershipCompanyAssignment(a.CompanyId, a.ValidFrom, a.ValidUntil, a.BranchId))
                .ToList();
            return await scope.CanManageAsync(membershipId, ct) && await companies.SetMembershipCompaniesAsync(membershipId, assignments, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }
}
