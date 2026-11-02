using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Communication;

/// <summary>
/// Atomik sıklık rezervasyonu (ADR-0025, dilim 5). Koşullu <c>UPDATE ... WHERE Count &lt; limit</c> tek ifadede sayar ve sınırı denetler, bu yüzden iki
/// kampanya aynı anda aynı adrese gönderirken toplam sınır aşılamaz. İlk satır benzersiz indeksle açılır; yarışı kaybeden yeniden güncellemeyi dener.
/// İzlenen entity kullanılmaz: bağlamdaki başka bekleyen değişikliğe dokunmaz. Tenant ve RLS oturum değerinden gelir.
/// </summary>
internal sealed class FrequencyLimiter(ModulixDbContext db, ITenantContextAccessor tenantAccessor) : IFrequencyLimiter
{
    public async Task<bool> TryReserveAsync(CommunicationChannel channel, string address, DateOnly day, int limit, CancellationToken cancellationToken)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Sınır en az 1 olmalıdır.");
        }

        var tenantId = RequireTenant();
        var channelName = channel.ToString();
        var date = day.ToDateTime(TimeOnly.MinValue);
        if (await IncrementAsync(tenantId, channelName, address, date, limit, cancellationToken) > 0)
        {
            return true;
        }

        if (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM [core].[FrequencyWindows]
                WHERE [TenantId] = {tenantId} AND [Channel] = {channelName} AND [Address] = {address} AND [Day] = {date}
                """).SingleAsync(cancellationToken) > 0)
        {
            return false;
        }

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [core].[FrequencyWindows] ([Id], [TenantId], [Channel], [Address], [Day], [Count])
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {channelName}, {address}, {date}, 1)
                """, cancellationToken);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Aynı anda başka bir kampanya ilk satırı açtı; şimdi güncellemeyi dene.
            return await IncrementAsync(tenantId, channelName, address, date, limit, cancellationToken) > 0;
        }
    }

    public async Task ReleaseAsync(CommunicationChannel channel, string address, DateOnly day, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var channelName = channel.ToString();
        var date = day.ToDateTime(TimeOnly.MinValue);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [core].[FrequencyWindows] SET [Count] = [Count] - 1
            WHERE [TenantId] = {tenantId} AND [Channel] = {channelName} AND [Address] = {address} AND [Day] = {date} AND [Count] > 0
            """, cancellationToken);
    }

    private Task<int> IncrementAsync(Guid tenantId, string channelName, string address, DateTime date, int limit, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [core].[FrequencyWindows] SET [Count] = [Count] + 1
            WHERE [TenantId] = {tenantId} AND [Channel] = {channelName} AND [Address] = {address} AND [Day] = {date} AND [Count] < {limit}
            """, cancellationToken);

    private Guid RequireTenant() =>
        tenantAccessor.Current?.TenantId ?? throw new InvalidOperationException("Aktif tenant olmadan sıklık sınırı kullanılamaz.");
}
