using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Settings;

internal sealed class TenantLogoStore(ModulixDbContext db, ITenantContextAccessor tenantAccessor, IAuditWriter audit) : ITenantLogo
{
    public async Task<TenantLogoInfo?> GetInfoAsync(CancellationToken cancellationToken) =>
        await db.TenantLogos.AsNoTracking()
            .Select(l => new TenantLogoInfo(l.ContentType, l.Data.Length, l.Version))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<(TenantLogoInfo Info, byte[] Data)?> GetAsync(CancellationToken cancellationToken)
    {
        var logo = await db.TenantLogos.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return logo is null ? null : (Info(logo), logo.Data);
    }

    public async Task<TenantLogoInfo> SaveAsync(byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (TenantLogo.DetectContentType(data) is null)
        {
            throw new ArgumentException(TenantLogo.InvalidMessage, "logo");
        }

        var logo = await db.TenantLogos.SingleOrDefaultAsync(cancellationToken);
        var before = logo?.Describe();
        if (logo is null)
        {
            logo = new TenantLogo(data);
            db.TenantLogos.Add(logo);
        }
        else
        {
            logo.Replace(data);
        }

        Audit(before, logo.Describe());
        await db.SaveChangesAsync(cancellationToken);
        return Info(logo);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (await db.TenantLogos.SingleOrDefaultAsync(cancellationToken) is not { } logo)
        {
            return;
        }

        Audit(logo.Describe(), null);
        db.TenantLogos.Remove(logo);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Logo firma bilgisidir: denetim kaydında "Firma bilgileri → Logo", eski ve yeni dosyanın özetiyle.
    private void Audit(string? before, string? after)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan logo değiştirilemez.");
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Tenant,
            tenant.TenantId,
            AuditAction.Updated,
            RecordDiff.Compute(new JsonObject { ["logo"] = before }, new JsonObject { ["logo"] = after }),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));
    }

    private static TenantLogoInfo Info(TenantLogo logo) => new(logo.ContentType, logo.Data.Length, logo.Version);
}
