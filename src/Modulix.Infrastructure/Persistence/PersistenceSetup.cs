using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Audit;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Modules;
using Modulix.Infrastructure.Platform;
using Modulix.Infrastructure.Tenancy;
using Modulix.Infrastructure.Users;

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

        services.AddSingleton<ITenantConnectionResolver>(new SharedDatabaseConnectionResolver(appConnectionString));

        // Seçenekler istek başına oluşturulur; böylece oturum değerini yazan interceptor o isteğin tenant'ını görür.
        services.AddDbContext<ModulixDbContext>((serviceProvider, options) =>
        {
            var tenantAccessor = serviceProvider.GetService<ITenantContextAccessor>();
            var connectionString = serviceProvider.GetRequiredService<ITenantConnectionResolver>()
                .GetConnectionString(tenantAccessor?.Current?.TenantId);

            options.UseModulixSqlServer(connectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenantAccessor));
        });
        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<ITenantMembers, TenantMembers>();
        services.AddScoped<IModuleData, ModuleData>();

        // Modül hakları ve izinler; ModuleCatalog'u host kaydeder (AddModulixModules).
        services.AddScoped<IModuleEntitlements, ModuleEntitlements>();
        services.AddScoped<AdministratorGuard>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<MembershipPermissions>();
        services.AddScoped<ICurrentAccess, CurrentAccess>();

        // Kullanıcı yönetimi (Y2); UserManager'ı host kaydeder (AddModulixAuth).
        services.AddScoped<UserAdministration>();
        services.AddScoped<IUserAdministration>(sp => sp.GetRequiredService<UserAdministration>());
        services.AddScoped<AccountLinks>();
        services.AddScoped<AccountAudit>();
        services.AddScoped<RecordHandover>();

        // Platform yönetimi (Y4); ITenantScopeFactory'yi host kaydeder (tenant bağlamını o kurar).
        services.AddScoped<IPlatformAdministration, PlatformAdministration>();

        // Denetim kaydı okuma (Y5).
        services.AddScoped<IAuditLog, AuditLog>();
        return services;
    }

    /// <summary>Bekleyen migration'ları migration kullanıcısıyla uygular. Model, uygulamadaki modüllerle aynı olmalıdır.</summary>
    public static async Task MigrateAsync(string migrationsConnectionString, ModuleCatalog modules, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);

        await using var context = CreateContext(migrationsConnectionString, modules);
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Tenant bağlamı olmayan bağlam: migration ve 'dotnet ef migrations add' (tasarım zamanı) içindir.
    /// Tenant tablolarında satır göstermez ve yazdırmaz.
    /// </summary>
    public static ModulixDbContext CreateContext(string connectionString, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var options = new DbContextOptionsBuilder<ModulixDbContext>();
        options.UseModulixSqlServer(connectionString);
        return new ModulixDbContext(options.Options, tenantAccessor: null, modules);
    }

    internal static DbContextOptionsBuilder UseModulixSqlServer(this DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, DatabaseSchemas.Platform))
            .ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>();
}
