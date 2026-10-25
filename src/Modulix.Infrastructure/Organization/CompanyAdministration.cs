using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>
/// İşletici şirketleri ve üyeliklerin şirket görevlerini yönetir (ADR-0008, adım 2). Yapılandırma servisidir: yetkiyi
/// uç nokta <c>core.companies.manage</c> ile denetler, değişiklik aynı işlemde denetim kaydına yazılır.
/// </summary>
internal sealed class CompanyAdministration(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit) : ICompanyAdministration
{
    public async Task<IReadOnlyList<CompanySummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.Companies.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CompanySummary(c.Id, c.Name, c.IsDefault))
            .ToListAsync(cancellationToken);

    public async Task<CompanySummary> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var company = new Company(name, isDefault: false);
        await EnsureUniqueNameAsync(company.Name, exceptCompanyId: null, cancellationToken);

        db.Companies.Add(company);
        AuditCompany(tenant, company.Id, AuditAction.Created, before: null, after: Snapshot(company));
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(company);
    }

    public async Task<CompanySummary?> RenameAsync(Guid companyId, string name, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return null;
        }

        var before = Snapshot(company);
        company.Rename(name);
        await EnsureUniqueNameAsync(company.Name, company.Id, cancellationToken);

        var after = Snapshot(company);
        if (!JsonNode.DeepEquals(before, after))
        {
            AuditCompany(tenant, company.Id, AuditAction.Updated, before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(company);
    }

    public async Task<IReadOnlyList<Guid>?> GetMembershipCompaniesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return null;
        }

        return await db.MembershipCompanies.AsNoTracking()
            .Where(mc => mc.MembershipId == membershipId)
            .Select(mc => mc.CompanyId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> SetMembershipCompaniesAsync(
        Guid membershipId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(companyIds);
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return false;
        }

        var wanted = companyIds.ToHashSet();
        var found = await db.Companies.Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        var missing = wanted.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Bu tenant'ta bulunmayan şirket: {string.Join(", ", missing)}.", nameof(companyIds));
        }

        var current = await db.MembershipCompanies.Where(mc => mc.MembershipId == membershipId).ToListAsync(cancellationToken);
        var currentIds = current.Select(mc => mc.CompanyId).ToHashSet();
        if (currentIds.SetEquals(wanted))
        {
            return true;
        }

        db.MembershipCompanies.RemoveRange(current.Where(mc => !wanted.Contains(mc.CompanyId)));
        foreach (var companyId in wanted.Except(currentIds))
        {
            db.MembershipCompanies.Add(new MembershipCompany(membershipId, companyId));
        }

        // Denetimde şirket adları gösterilir; görev değişikliği üyeliğin kaydına yazılır.
        var names = await db.Companies.AsNoTracking()
            .Where(c => currentIds.Contains(c.Id) || wanted.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Membership,
            membershipId,
            AuditAction.Updated,
            RecordDiff.Compute(
                new JsonObject { ["companies"] = CompanyNames(currentIds, names) },
                new JsonObject { ["companies"] = CompanyNames(wanted, names) }),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan şirketler yönetilemez.");

    // Üyelikler platform tablosundadır (RLS dışı); tenant'a ait olduğu burada açıkça denetlenir.
    private Task<bool> MembershipBelongsToTenantAsync(Guid membershipId, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships.AnyAsync(m => m.Id == membershipId && m.TenantId == tenantId, cancellationToken);

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptCompanyId, CancellationToken cancellationToken)
    {
        if (await db.Companies.AnyAsync(c => c.Name == name && c.Id != exceptCompanyId, cancellationToken))
        {
            throw new CompanyConflictException($"Bu adda bir şirket zaten var: '{name}'.");
        }
    }

    private void AuditCompany(TenantContext tenant, Guid companyId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Company,
            companyId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(Company company) => new()
    {
        ["name"] = company.Name,
        ["isDefault"] = company.IsDefault,
    };

    private static CompanySummary ToSummary(Company company) => new(company.Id, company.Name, company.IsDefault);

    private static JsonArray CompanyNames(IEnumerable<Guid> companyIds, IReadOnlyDictionary<Guid, string> names) =>
        new([.. companyIds.Select(id => names.GetValueOrDefault(id, id.ToString())).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n))]);
}
