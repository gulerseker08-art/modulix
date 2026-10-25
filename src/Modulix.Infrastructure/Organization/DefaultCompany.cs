using Microsoft.EntityFrameworkCore;
using Modulix.Core.Organization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>Her tenant'ın en az bir işletici şirketi vardır (ADR-0008); tenant açılırken tenant adıyla varsayılan şirket oluşur.</summary>
public static class DefaultCompany
{
    /// <summary>Aktif tenant'ın şirketi yoksa varsayılan şirketi ekler (kaydetmez). Tekrar çağırmak güvenlidir.</summary>
    public static async Task EnsureAsync(ModulixDbContext db, string tenantName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Companies.AnyAsync(cancellationToken))
        {
            return;
        }

        var name = tenantName.Trim();
        db.Companies.Add(new Company(name.Length > Company.NameMaxLength ? name[..Company.NameMaxLength] : name, isDefault: true));
    }
}
