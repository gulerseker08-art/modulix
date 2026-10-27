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
    PlatformDbContext platform,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    TimeProvider time) : ICompanyAdministration
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

    public async Task<IReadOnlyList<MembershipCompanyAssignment>?> GetMembershipCompaniesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return null;
        }

        return await db.MembershipCompanies.AsNoTracking()
            .Where(mc => mc.MembershipId == membershipId)
            .Select(mc => new MembershipCompanyAssignment(mc.CompanyId, mc.ValidFrom, mc.ValidUntil, mc.BranchId))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> SetMembershipCompaniesAsync(
        Guid membershipId, IReadOnlyCollection<MembershipCompanyAssignment> assignments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return false;
        }

        var wanted = new Dictionary<Guid, MembershipCompanyAssignment>();
        foreach (var assignment in assignments)
        {
            if (!wanted.TryAdd(assignment.CompanyId, assignment))
            {
                throw new ArgumentException("Aynı şirket birden fazla kez görevlendirilemez.", nameof(assignments));
            }
        }

        // Şirketler bu tenant'ta olmalı.
        var found = await db.Companies.Where(c => wanted.Keys.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        var missing = wanted.Keys.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Bu tenant'ta bulunmayan şirket: {string.Join(", ", missing)}.", nameof(assignments));
        }

        // Seçilen varsayılan şubeler, görevlendirilen şirkete ait olmalı (şube kapsam sınırı değildir, idari bağdır).
        var branchIds = assignments.Where(a => a.BranchId is not null).Select(a => a.BranchId!.Value).ToHashSet();
        var branchCompanies = branchIds.Count == 0
            ? []
            : await db.Branches.AsNoTracking()
                .Where(b => branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.CompanyId, cancellationToken);
        foreach (var assignment in assignments.Where(a => a.BranchId is not null))
        {
            if (!branchCompanies.TryGetValue(assignment.BranchId!.Value, out var branchCompanyId) || branchCompanyId != assignment.CompanyId)
            {
                throw new ArgumentException("Varsayılan şube, görevlendirilen şirkete ait olmalıdır.", nameof(assignments));
            }
        }

        var current = await db.MembershipCompanies.Where(mc => mc.MembershipId == membershipId).ToListAsync(cancellationToken);
        var currentByCompany = current.ToDictionary(mc => mc.CompanyId);

        // Denetim "önce" anlık görüntüsü değişiklikten önce alınır.
        var before = current
            .Select(mc => (mc.CompanyId, mc.ValidFrom, mc.ValidUntil, mc.BranchId))
            .ToList();
        var now = time.GetUtcNow();

        var changed = false;
        foreach (var removed in current.Where(mc => !wanted.ContainsKey(mc.CompanyId)))
        {
            db.MembershipCompanies.Remove(removed);
            changed = true;
        }

        foreach (var (companyId, assignment) in wanted)
        {
            if (currentByCompany.TryGetValue(companyId, out var existing))
            {
                var validityChanged = existing.ValidFrom != assignment.ValidFrom || existing.ValidUntil != assignment.ValidUntil;
                var branchChanged = existing.BranchId != (assignment.BranchId == Guid.Empty ? null : assignment.BranchId);
                if (validityChanged)
                {
                    RejectPastExpiry(assignment.ValidUntil, now);
                    existing.SetValidity(assignment.ValidFrom, assignment.ValidUntil);
                }

                if (branchChanged)
                {
                    existing.SetBranch(assignment.BranchId);
                }

                changed |= validityChanged || branchChanged;
            }
            else
            {
                RejectPastExpiry(assignment.ValidUntil, now);
                db.MembershipCompanies.Add(
                    new MembershipCompany(membershipId, companyId, assignment.ValidFrom, assignment.ValidUntil, assignment.BranchId));
                changed = true;
            }
        }

        if (!changed)
        {
            return true;
        }

        // Denetimde şirket adı, vekâlet aralığı ve varsayılan şube gösterilir; görev değişikliği üyeliğin kaydına yazılır.
        var companyNames = await db.Companies.AsNoTracking()
            .Where(c => currentByCompany.Keys.Contains(c.Id) || wanted.Keys.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var branchNames = await db.Branches.AsNoTracking()
            .Where(b => branchIds.Contains(b.Id) || before.Select(x => x.BranchId).Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Membership,
            membershipId,
            AuditAction.Updated,
            RecordDiff.Compute(
                new JsonObject { ["companies"] = DescribeAssignments(before, companyNames, branchNames) },
                new JsonObject
                {
                    ["companies"] = DescribeAssignments(
                        wanted.Values.Select(a => (a.CompanyId, a.ValidFrom, a.ValidUntil, a.BranchId == Guid.Empty ? null : a.BranchId)),
                        companyNames,
                        branchNames),
                }),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void RejectPastExpiry(DateTimeOffset? validUntil, DateTimeOffset now)
    {
        if (validUntil is { } until && until <= now)
        {
            throw new ArgumentException("Vekâletin bitişi gelecekte olmalıdır.", nameof(validUntil));
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan şirketler yönetilemez.");

    // Üyelikler platform DB'sindedir (ADR-0009); atamadan önce üyeliğin bu tenant'a ait olduğu açıkça denetlenir.
    private Task<bool> MembershipBelongsToTenantAsync(Guid membershipId, Guid tenantId, CancellationToken cancellationToken) =>
        platform.Memberships.AnyAsync(m => m.Id == membershipId && m.TenantId == tenantId, cancellationToken);

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

    // Denetim için görev açıklaması: "Depo A.Ş.", vekâlet aralığı ve varsayılan şube eklenir.
    private static JsonArray DescribeAssignments(
        IEnumerable<(Guid CompanyId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, Guid? BranchId)> assignments,
        IReadOnlyDictionary<Guid, string> companyNames,
        IReadOnlyDictionary<Guid, string> branchNames) =>
        new([.. assignments
            .Select(a => Describe(a, companyNames, branchNames))
            .Order(StringComparer.Ordinal)
            .Select(text => JsonValue.Create(text))]);

    private static string Describe(
        (Guid CompanyId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, Guid? BranchId) assignment,
        IReadOnlyDictionary<Guid, string> companyNames,
        IReadOnlyDictionary<Guid, string> branchNames)
    {
        var text = companyNames.GetValueOrDefault(assignment.CompanyId, assignment.CompanyId.ToString());
        if (assignment.BranchId is { } branchId)
        {
            text += $" — {branchNames.GetValueOrDefault(branchId, branchId.ToString())} şubesi";
        }

        if (assignment.ValidFrom is { } from && assignment.ValidUntil is { } until)
        {
            text += $" ({from:dd.MM.yyyy}–{until:dd.MM.yyyy} vekâlet)";
        }
        else if (assignment.ValidUntil is { } onlyUntil)
        {
            text += $" ({onlyUntil:dd.MM.yyyy} tarihine kadar vekâlet)";
        }
        else if (assignment.ValidFrom is { } onlyFrom)
        {
            text += $" ({onlyFrom:dd.MM.yyyy} tarihinden itibaren)";
        }

        return text;
    }
}
