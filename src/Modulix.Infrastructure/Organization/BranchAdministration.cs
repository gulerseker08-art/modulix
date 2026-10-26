using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>
/// İşletici şirketlerin şubelerini yönetir (ADR-0008, adım 5). Şube idari bir birimdir; kapsam sınırı değildir ve hiçbir
/// kaydı süzmez. Yetkiyi uç nokta <c>core.companies.manage</c> ile denetler, değişiklik aynı işlemde denetim kaydına yazılır.
/// </summary>
internal sealed class BranchAdministration(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit) : IBranchAdministration
{
    public async Task<IReadOnlyList<BranchSummary>?> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        RequireTenant();
        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            return null;
        }

        return await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId)
            .OrderBy(b => b.Name)
            .Select(b => new BranchSummary(b.Id, b.CompanyId, b.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<BranchSummary?> CreateAsync(Guid companyId, string name, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return null;
        }

        var branch = new Branch(companyId, name);
        await EnsureUniqueNameAsync(companyId, branch.Name, exceptBranchId: null, cancellationToken);

        db.Branches.Add(branch);
        Audit(tenant, branch.Id, AuditAction.Created, before: null, after: Snapshot(branch, company.Name));
        await db.SaveChangesAsync(cancellationToken);
        return new BranchSummary(branch.Id, branch.CompanyId, branch.Name);
    }

    public async Task<BranchSummary?> RenameAsync(Guid companyId, Guid branchId, string name, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == branchId && b.CompanyId == companyId, cancellationToken);
        if (branch is null)
        {
            return null;
        }

        var companyName = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).SingleAsync(cancellationToken);
        var before = Snapshot(branch, companyName);
        branch.Rename(name);
        await EnsureUniqueNameAsync(companyId, branch.Name, branch.Id, cancellationToken);

        var after = Snapshot(branch, companyName);
        if (!JsonNode.DeepEquals(before, after))
        {
            Audit(tenant, branch.Id, AuditAction.Updated, before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new BranchSummary(branch.Id, branch.CompanyId, branch.Name);
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan şubeler yönetilemez.");

    private async Task EnsureUniqueNameAsync(Guid companyId, string name, Guid? exceptBranchId, CancellationToken cancellationToken)
    {
        if (await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Name == name && b.Id != exceptBranchId, cancellationToken))
        {
            throw new CompanyConflictException($"Bu adda bir şube zaten var: '{name}'.");
        }
    }

    private void Audit(TenantContext tenant, Guid branchId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Branch,
            branchId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(Branch branch, string companyName) => new()
    {
        ["name"] = branch.Name,
        ["company"] = companyName,
    };
}
