using Microsoft.EntityFrameworkCore;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Identity;

/// <summary>Kişinin açık oturumlarından biri; <see cref="Device"/> tarayıcı bilgisinden okunur (ör. "Chrome · Windows").</summary>
public sealed record OpenSession(Guid Id, string Device, string? IpAddress, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

/// <summary>
/// Sunucudaki oturumlar (Y7a). Giriş oturum başlatır, çıkış kapatır; şifre veya e-posta değişince, kullanıcı devre dışı
/// bırakılınca ya da kişi veya yöneticisi istediğinde oturumlar kapanır. Kapatılan oturumun çerezi bir sonraki istekte
/// geçersiz olur.
/// </summary>
public sealed class UserSessions(PlatformDbContext db, TimeProvider time)
{
    /// <summary>Son etkinlik en fazla bu aralıkla yazılır (her istekte yazmamak için).</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    /// <summary>Bu süre işlem yapılmayan oturumun çerezi geçersizdir (çerezin süresiyle aynı).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    /// <summary>Kapanmış ve süresi dolmuş oturumlar bu süreden sonra silinir.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<Guid> StartAsync(Guid userId, AccountClient client, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cutoff = now - Retention;
        await db.UserSessions
            .Where(s => s.UserId == userId && (s.ClosedAt < cutoff || s.LastSeenAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);

        var session = new UserSession(userId, client, now);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    /// <summary>Oturum açık mı; açıksa son etkinliği gerektiğinde günceller.</summary>
    public async Task<bool> ValidateAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var session = await db.UserSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new { s.ClosedAt, s.LastSeenAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null || session.ClosedAt is not null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (now - session.LastSeenAt >= TouchInterval)
        {
            await db.UserSessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<OpenSession>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var idleSince = time.GetUtcNow() - IdleTimeout;
        var sessions = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ClosedAt == null && s.LastSeenAt > idleSince)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken);
        return sessions.Select(s => new OpenSession(s.Id, DeviceName.Of(s.UserAgent), s.IpAddress, s.CreatedAt, s.LastSeenAt)).ToList();
    }

    /// <summary>Tek oturumu kapatır; başka kişinin oturumu ya da zaten kapalıysa <c>false</c>.</summary>
    public async Task<bool> CloseAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        await CloseWhereAsync(userId, sessionId, null, cancellationToken) > 0;

    /// <summary>Kişinin açık oturumlarını kapatır (<paramref name="except"/> hariç); kapatılan sayısını döner.</summary>
    public Task<int> CloseAllAsync(Guid userId, Guid? except, CancellationToken cancellationToken) =>
        CloseWhereAsync(userId, null, except, cancellationToken);

    private Task<int> CloseWhereAsync(Guid userId, Guid? only, Guid? except, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var idleSince = now - IdleTimeout;
        var query = db.UserSessions.Where(s => s.UserId == userId && s.ClosedAt == null && s.LastSeenAt > idleSince);
        if (only is { } id)
        {
            query = query.Where(s => s.Id == id);
        }

        if (except is { } kept)
        {
            query = query.Where(s => s.Id != kept);
        }

        return query.ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedAt, now), cancellationToken);
    }
}

/// <summary>Tarayıcı bilgisinden okunabilir cihaz adı: "Chrome · Windows". Bilinmiyorsa "Bilinmeyen tarayıcı".</summary>
public static class DeviceName
{
    public static string Of(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Bilinmeyen tarayıcı";
        }

        string? browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when userAgent.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            _ when userAgent.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            _ when userAgent.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
            _ when userAgent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => null,
        };
        string? system = userAgent switch
        {
            _ when userAgent.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when userAgent.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when userAgent.Contains("iPhone", StringComparison.Ordinal) || userAgent.Contains("iPad", StringComparison.Ordinal) => "iOS",
            _ when userAgent.Contains("Mac OS X", StringComparison.Ordinal) => "macOS",
            _ when userAgent.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null,
        };

        return (browser, system) switch
        {
            (null, null) => "Bilinmeyen tarayıcı",
            (null, { } os) => os,
            ({ } name, null) => name,
            ({ } name, { } os) => $"{name} · {os}",
        };
    }
}
