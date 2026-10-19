using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Modulix.Infrastructure.Persistence;

public static class PersistenceSetup
{
    /// <summary>Uygulamanın bağlantısı (modulix_app): veri okur/yazar, şema değiştiremez.</summary>
    public const string AppConnectionName = "Modulix";

    /// <summary>Migration bağlantısı (modulix_migrator): yalnızca "migrate" komutu kullanır.</summary>
    public const string MigrationsConnectionName = "ModulixMigrations";

    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public static IServiceCollection AddModulixPersistence(this IServiceCollection services, string appConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appConnectionString);

        services.AddDbContext<ModulixDbContext>(options => options.UseModulixSqlServer(appConnectionString));
        return services;
    }

    /// <summary>Bekleyen migration'ları migration kullanıcısıyla uygular.</summary>
    public static async Task MigrateAsync(string migrationsConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);

        var options = new DbContextOptionsBuilder<ModulixDbContext>();
        options.UseModulixSqlServer(migrationsConnectionString);

        await using var context = new ModulixDbContext(options.Options);
        await context.Database.MigrateAsync(cancellationToken);
    }

    internal static DbContextOptionsBuilder UseModulixSqlServer(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(
            connectionString,
            sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DatabaseSchemas.Platform));
}
