using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Users;

/// <summary>"App" yapılandırma bölümü: e-postadaki bağlantıların kökü (App:PublicUrl). İstek dışında (olay abonesi) kullanılır.</summary>
public sealed class PublicLinkOptions
{
    public const string SectionName = "App";

    public string? PublicUrl { get; set; }
}

/// <summary>
/// Bildirim alıcıları (ADR-0007). Kayıt, alıcının kendi kimliğiyle açılan ayrı bir kapsamda kayıt boru hattından okunur:
/// izin, kayıt kapsamı ve hassas alanlar kişinin kendi isteğindeki gibidir (olay aboneliğinin izni karışmaz).
/// </summary>
internal sealed class NotificationRecipients(
    ITenantContextAccessor tenantAccessor,
    PlatformDbContext db,
    IMembershipDirectory memberships,
    ITenantScopeFactory tenantScopes,
    IOptions<PublicLinkOptions> links) : INotificationRecipients
{
    public async Task<NotificationRecipient?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (tenantAccessor.Current is not { } tenant || await memberships.FindActiveAsync(userId, tenant.TenantId, cancellationToken) is null)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.Email })
            .SingleOrDefaultAsync(cancellationToken);
        return user?.Email is { Length: > 0 } email ? new NotificationRecipient(userId, user.DisplayName, email) : null;
    }

    public async Task<RecordData?> ReadRecordAsync(NotificationRecipient recipient, RecordRef record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (tenantAccessor.Current is not { } tenant
            || await memberships.FindActiveAsync(recipient.UserId, tenant.TenantId, cancellationToken) is not { } membership)
        {
            return null;
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenant.TenantId, recipient.UserId, membership.MembershipId));
        var result = await scope.ServiceProvider.GetRequiredService<IRecordPipeline>().GetAsync(record, cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    public string? Link(string path) =>
        links.Value.PublicUrl is { Length: > 0 } root ? $"{root.TrimEnd('/')}/{path.TrimStart('/')}" : null;
}
