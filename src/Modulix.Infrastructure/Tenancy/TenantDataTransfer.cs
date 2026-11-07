using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>Bir tablodan dışa aktarılan veya silinen satır sayısı.</summary>
public sealed record TenantTableCount(string Schema, string Table, int Rows);

public sealed record TenantExportResult(Guid TenantId, string Slug, string Directory, IReadOnlyList<TenantTableCount> Tables)
{
    public int TotalRows => Tables.Sum(t => t.Rows);
}

public sealed record TenantDeleteResult(Guid TenantId, string Slug, IReadOnlyList<TenantTableCount> Tables)
{
    public int TotalRows => Tables.Sum(t => t.Rows);
}

/// <summary>
/// Bir tenant'ın bütün verisini <c>TenantId</c> ile eksiksiz dışa aktarır ve siler (proje kuralı; ADR-0020 Dilim 3).
/// Tablolar koddan değil veritabanı kataloğundan bulunur: <c>TenantId</c> kolonu taşıyan her tablo (tenant şemaları ve
/// platform şemasındaki üyelik, modül hakkı, e-posta, outbox, teslim) kapsamdadır; yeni tablo eklenince komut kendiliğinden
/// onu da kapsar. Kullanıcı hesapları (<c>platform.Users</c>) tenant'a değil kişiye aittir; silinmez, yalnızca üyelikleri
/// kalkar. Migration kullanıcısıyla çalışır ve oturum tenant'ını yazar: Row-Level Security ikinci katman olarak yalnızca
/// o tenant'ın satırlarını gösterir, her sorguda ayrıca açık <c>TenantId</c> koşulu vardır.
/// </summary>
public interface ITenantDataTransfer
{
    /// <summary>Tenant'ın verisini verilen dizine tablo başına bir JSON dosyası olarak yazar (<c>tenant.json</c> ve <c>manifest.json</c> ile).</summary>
    Task<TenantExportResult> ExportAsync(Guid tenantId, string directory, CancellationToken cancellationToken);

    /// <summary>Tenant'ın bütün satırlarını ve tenant kaydını tek işlemde siler. Geri alınamaz; önce dışa aktarılmalıdır.</summary>
    Task<TenantDeleteResult> DeleteAsync(Guid tenantId, CancellationToken cancellationToken);
}

internal sealed class TenantDataTransfer(ModulixDbContext db, ModulixDatabaseOptions database, ILogger<TenantDataTransfer> logger) : ITenantDataTransfer
{
    private const string TenantColumn = "TenantId";

    // Türkçe karakterler XXXX olarak değil olduğu gibi yazılır: dışa aktarılan dosya insan tarafından okunabilir olmalıdır.
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<TenantExportResult> ExportAsync(Guid tenantId, string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var tenant = await FindAsync(tenantId, cancellationToken);
        Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(tenantId, cancellationToken);
        var tables = await TenantTablesAsync(connection, cancellationToken);
        var counts = new List<TenantTableCount>(tables.Count);
        foreach (var (schema, table) in tables)
        {
            counts.Add(new TenantTableCount(schema, table, await ExportTableAsync(connection, tenantId, schema, table, directory, cancellationToken)));
        }

        await WriteJsonAsync(Path.Combine(directory, "tenant.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", tenant.Id);
            writer.WriteString("name", tenant.Name);
            writer.WriteString("slug", tenant.Slug);
            writer.WriteString("status", tenant.Status.ToString());
            writer.WriteString("createdAt", tenant.CreatedAt);
            writer.WriteString("profileKey", tenant.ProfileKey);
            writer.WriteEndObject();
        }, cancellationToken);

        await WriteJsonAsync(Path.Combine(directory, "manifest.json"), writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tenantId", tenant.Id);
            writer.WriteString("slug", tenant.Slug);
            writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
            writer.WriteStartArray("tables");
            foreach (var count in counts)
            {
                writer.WriteStartObject();
                writer.WriteString("schema", count.Schema);
                writer.WriteString("table", count.Table);
                writer.WriteNumber("rows", count.Rows);
                writer.WriteString("file", FileName(count.Schema, count.Table));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, cancellationToken);

        logger.LogInformation("Tenant {Slug} ({TenantId}) dışa aktarıldı: {Tables} tablo, {Rows} satır, dizin {Directory}.",
            tenant.Slug, tenant.Id, counts.Count, counts.Sum(c => c.Rows), directory);
        await AuditAsync(PlatformAuditActions.TenantExported, tenant, counts, directory, cancellationToken);
        return new TenantExportResult(tenant.Id, tenant.Slug, directory, counts);
    }

    public async Task<TenantDeleteResult> DeleteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await FindAsync(tenantId, cancellationToken);

        await using var connection = await OpenAsync(tenantId, cancellationToken);
        var remaining = await TenantTablesAsync(connection, cancellationToken);
        var counts = new List<TenantTableCount>(remaining.Count);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Stok hareket defteri yalnızca eklenir (ADR-0032 K13): veritabanı tetikleyicisi silmeyi reddeder. Tenant verisinin tamamen silinmesi tek meşru istisnadır
        // ve yalnızca bu bağlantı için bayrakla açılır (oturum değeri bağlantı havuzuna dönerken sıfırlanır; uygulamanın normal yazma yolu bayrağı hiç kurmaz).
        await using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = "EXEC sp_set_session_context N'AllowLedgerPurge', 1;";
            await purge.ExecuteNonQueryAsync(cancellationToken);
        }

        // Yabancı anahtar sırası koddan değil hatadan öğrenilir: bir turda silinemeyen (başka tablonun satırları ona bağlı)
        // tablo sonraki turda yeniden denenir; bir turda hiç ilerleme olmazsa döngüsel bağımlılık var demektir ve işlem geri alınır.
        while (remaining.Count > 0)
        {
            var blocked = new List<(string Schema, string Table)>();
            foreach (var (schema, table) in remaining)
            {
                try
                {
                    await using var savepoint = connection.CreateCommand();
                    savepoint.Transaction = transaction;
                    savepoint.CommandText = "SAVE TRANSACTION TenantDelete;";
                    await savepoint.ExecuteNonQueryAsync(cancellationToken);

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {Quote(schema, table)} WHERE [{TenantColumn}] = @tenant;";
                    command.Parameters.AddWithValue("@tenant", tenantId);
                    counts.Add(new TenantTableCount(schema, table, await command.ExecuteNonQueryAsync(cancellationToken)));
                }
                catch (SqlException exception) when (exception.Number == 547)
                {
                    await using var rollback = connection.CreateCommand();
                    rollback.Transaction = transaction;
                    rollback.CommandText = "ROLLBACK TRANSACTION TenantDelete;";
                    await rollback.ExecuteNonQueryAsync(cancellationToken);
                    blocked.Add((schema, table));
                }
            }

            if (blocked.Count == remaining.Count)
            {
                throw new InvalidOperationException(
                    "Tenant verisi silinemedi: yabancı anahtar bağımlılığı çözülemiyor (" + string.Join(", ", blocked.Select(b => $"{b.Schema}.{b.Table}")) + ").");
            }

            remaining = blocked;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM [{DatabaseSchemas.Platform}].[Tenants] WHERE [Id] = @tenant;";
            command.Parameters.AddWithValue("@tenant", tenantId);
            counts.Add(new TenantTableCount(DatabaseSchemas.Platform, "Tenants", await command.ExecuteNonQueryAsync(cancellationToken)));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning("Tenant {Slug} ({TenantId}) silindi: {Tables} tablodan {Rows} satır.",
            tenant.Slug, tenant.Id, counts.Count, counts.Sum(c => c.Rows));
        await AuditAsync(PlatformAuditActions.TenantDeleted, tenant, counts, null, cancellationToken);
        return new TenantDeleteResult(tenant.Id, tenant.Slug, counts);
    }

    /// <summary>
    /// Platform denetim kaydı (ADR-0028): dışa aktarma ve silme komut satırından çalışır, oturum açmış kullanıcı yoktur; aktör boş kimliktir ve
    /// kaynak ayrıntıda "cli" olarak yazılır. Kayıt tenant silindikten sonra da kalır (<c>TargetTenantId</c> yabancı anahtar değildir).
    /// </summary>
    private async Task AuditAsync(string action, Tenant tenant, IReadOnlyList<TenantTableCount> counts, string? directory, CancellationToken cancellationToken)
    {
        var details = new JsonObject
        {
            ["source"] = "cli",
            ["slug"] = tenant.Slug,
            ["name"] = tenant.Name,
            ["tables"] = counts.Count,
            ["rows"] = counts.Sum(c => c.Rows),
        };
        if (directory is not null)
        {
            details["directory"] = directory;
        }

        db.PlatformAuditLog.Add(new PlatformAuditEntry(Guid.Empty, action, tenant.Id, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Tenant> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
        ?? throw new InvalidOperationException($"Tenant bulunamadı: {tenantId}.");

    /// <summary>Migration kullanıcısıyla bağlanır ve oturum tenant'ını yazar (RLS yalnızca bu tenant'ın satırlarını gösterir).</summary>
    private async Task<SqlConnection> OpenAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(database.MigrationsConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var session = connection.CreateCommand();
        session.CommandText = $"EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @tenant, @read_only = 1;";
        session.Parameters.AddWithValue("@tenant", tenantId);
        await session.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>Veritabanı kataloğundan TenantId kolonu taşıyan bütün tablolar (migration geçmişi hariç).</summary>
    private static async Task<List<(string Schema, string Table)>> TenantTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.name, t.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name = N'{TenantColumn}'
            WHERE t.name <> N'__EFMigrationsHistory'
            ORDER BY s.name, t.name;
            """;
        var tables = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    private static async Task<int> ExportTableAsync(
        SqlConnection connection, Guid tenantId, string schema, string table, string directory, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(schema, table)} WHERE [{TenantColumn}] = @tenant;";
        command.Parameters.AddWithValue("@tenant", tenantId);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using var stream = File.Create(Path.Combine(directory, FileName(schema, table)));
        await using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartArray();
        while (await reader.ReadAsync(cancellationToken))
        {
            writer.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                writer.WritePropertyName(reader.GetName(i));
                WriteValue(writer, await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i));
            }

            writer.WriteEndObject();
            rows++;
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        return rows;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte or short or int or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case double or float:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case DateTimeOffset at:
                writer.WriteStringValue(at);
                break;
            case DateTime at:
                writer.WriteStringValue(at);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static async Task WriteJsonAsync(string path, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new Utf8JsonWriter(stream, WriterOptions);
        write(writer);
        await writer.FlushAsync(cancellationToken);
    }

    private static string FileName(string schema, string table) => $"{schema}.{table}.json";

    private static string Quote(string schema, string table) => $"[{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]";
}
