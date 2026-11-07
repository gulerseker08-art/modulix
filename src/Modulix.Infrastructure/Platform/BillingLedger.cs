using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Platform;

/// <summary>Sağlayıcıdan bağımsız ödeme defteri (ADR-0028 Dilim 7c). Erişimi değiştirmez; silmez.</summary>
internal sealed class BillingLedger(ModulixDbContext db, TimeProvider time) : IBillingLedger
{
    public async Task<BillingInfo> CreateAsync(Guid tenantId, BillingInput input, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Firma bulunamadı.");
        var record = new BillingRecord(
            tenantId, input.PeriodStart, input.PeriodEnd, input.Amount, input.Currency, input.DueDate, input.Reference, input.Note,
            actingUserId, time.GetUtcNow());

        db.BillingRecords.Add(record);
        Audit(actingUserId, PlatformAuditActions.BillingCreated, record, null);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(record, tenant.Name);
    }

    public async Task<BillingInfo> MarkPaidAsync(Guid id, DateTimeOffset? paidAt, string? reference, Guid actingUserId, CancellationToken cancellationToken)
    {
        var record = await FindAsync(id, cancellationToken);
        EnsurePending(record, "ödendi işaretlenemez");
        var now = time.GetUtcNow();
        if (paidAt is { } at && at > now.AddDays(1))
        {
            throw new ArgumentException("Ödeme tarihi gelecekte olamaz.", "paidAt");
        }

        record.MarkPaid(paidAt ?? now, reference, actingUserId);
        Audit(actingUserId, PlatformAuditActions.BillingPaid, record, null);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(record, await TenantNameAsync(record.TenantId, cancellationToken));
    }

    public async Task<BillingInfo> CancelAsync(Guid id, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        var record = await FindAsync(id, cancellationToken);
        EnsurePending(record, "iptal edilemez");
        record.Cancel(time.GetUtcNow(), reason, actingUserId);
        Audit(actingUserId, PlatformAuditActions.BillingCancelled, record, record.CancelReason);
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(record, await TenantNameAsync(record.TenantId, cancellationToken));
    }

    public async Task<IReadOnlyList<BillingInfo>> ListAsync(Guid? tenantId, BillingStatus? status, CancellationToken cancellationToken)
    {
        var rows = await (
            from record in db.BillingRecords.AsNoTracking()
            where (tenantId == null || record.TenantId == tenantId) && (status == null || record.Status == status)
            join tenant in db.Tenants.AsNoTracking() on record.TenantId equals tenant.Id
            orderby record.CreatedAt descending, record.Id descending
            select new { Record = record, TenantName = tenant.Name })
            .Take(200)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => ToInfo(r.Record, r.TenantName))];
    }

    private async Task<BillingRecord> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.BillingRecords.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Ödeme kaydı bulunamadı.");

    private static void EnsurePending(BillingRecord record, string what)
    {
        if (record.Status != BillingStatus.Pending)
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, $"Yalnızca bekleyen kayıt {what}; bu kayıt zaten kapalı.");
        }
    }

    private Task<string> TenantNameAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(cancellationToken);

    private void Audit(Guid actingUserId, string action, BillingRecord record, string? reason) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(
            actingUserId,
            action,
            record.TenantId,
            reason,
            new JsonObject
            {
                ["billingId"] = record.Id,
                ["period"] = $"{record.PeriodStart:yyyy-MM-dd}/{record.PeriodEnd:yyyy-MM-dd}",
                ["amount"] = record.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["currency"] = record.Currency,
                ["reference"] = record.Reference,
            }.ToJsonString(),
            Activity.Current?.TraceId.ToString()));

    private BillingInfo ToInfo(BillingRecord record, string tenantName) => new(
        record.Id, record.TenantId, tenantName, record.PeriodStart, record.PeriodEnd, record.Amount, record.Currency, record.DueDate,
        record.Reference, record.Note, record.Status, record.PaidAt, record.CancelReason, record.CreatedAt,
        record.IsOverdueAt(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)));
}
