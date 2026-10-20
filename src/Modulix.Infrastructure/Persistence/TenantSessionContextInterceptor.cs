using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Her bağlantı açılışında aktif tenant'ı SQL Server oturumuna salt okunur olarak yazar.
/// Row-Level Security bu değeri kullanır. Salt okunur olduğu için aynı bağlantıda sonradan
/// değiştirilemez; bağlantı havuza dönüp yeniden kullanıldığında SQL Server değeri temizler.
/// Aktif tenant yoksa değer yazılmaz ve hiçbir tenant satırı görünmez.
/// </summary>
internal sealed class TenantSessionContextInterceptor(ITenantContextAccessor? tenantAccessor) : DbConnectionInterceptor
{
    private const string Sql =
        $"EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @tenantId, @read_only = 1;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (tenantAccessor?.Current is { } tenant)
        {
            using var command = CreateCommand(connection, tenant.TenantId);
            command.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (tenantAccessor?.Current is { } tenant)
        {
            await using var command = CreateCommand(connection, tenant.TenantId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, Guid tenantId)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenantId";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        return command;
    }
}
