using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Audit;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Core.Teams;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Customization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Modules;
using Modulix.Infrastructure.Organization;
using Modulix.Infrastructure.Platform;
using Modulix.Infrastructure.Settings;
using Modulix.Infrastructure.Teams;
using Modulix.Infrastructure.Tenancy;
using Modulix.Infrastructure.Users;

namespace Modulix.Infrastructure.Persistence;

public static class PersistenceSetup
{
    /// <summary>Platform uygulama bağlantısı (modulix_app): veri okur/yazar, şema değiştiremez.</summary>
    public const string AppConnectionName = "Modulix";

    /// <summary>Migration bağlantısı (modulix_migrator): 'migrate'/'migrate-tenants' komutları ve provisioning kullanır.</summary>
    public const string MigrationsConnectionName = "ModulixMigrations";

    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Platform migration geçmişi (platform DB'sinde).</summary>
    private const string PlatformHistorySchema = DatabaseSchemas.Platform;

    /// <summary>Tenant migration geçmişi (her tenant DB'sinde); core şeması tenant DB'lerinde her zaman vardır.</summary>
    private const string TenantHistorySchema = DatabaseSchemas.Core;

    public static IServiceCollection AddModulixPersistence(
        this IServiceCollection services, string platformAppConnectionString, string platformMigrationsConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformAppConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformMigrationsConnectionString);

        services.AddMemoryCache();
        services.AddSingleton(new PlatformDatabaseOptions(platformAppConnectionString, platformMigrationsConnectionString));
        services.AddSingleton<ITenantDatabaseRegistry, TenantDatabaseRegistry>();
        services.AddSingleton<ITenantConnectionResolver, TenantConnectionResolver>();

        // Platform bağlamı: sabit merkezi veritabanı (ADR-0009).
        services.AddDbContext<PlatformDbContext>(options => options.UsePlatformSqlServer(platformAppConnectionString));

        // Tenant bağlamı: bağlantı istek başına aktif tenant'tan çözülür; interceptor o isteğin tenant'ını (RLS) yazar.
        services.AddDbContext<ModulixDbContext>((serviceProvider, options) =>
        {
            var tenantAccessor = serviceProvider.GetService<ITenantContextAccessor>();
            var connectionString = serviceProvider.GetRequiredService<ITenantConnectionResolver>()
                .GetConnectionString(tenantAccessor?.Current?.TenantId);

            options.UseTenantSqlServer(connectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenantAccessor));
        });

        // Tenant kurulumu ve migration runner (ADR-0009).
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<TenantMigrationRunner>();

        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<ITenantMembers, TenantMembers>();
        services.AddScoped<IModuleData, ModuleData>();

        // Modül hakları ve izinler; ModuleCatalog'u host kaydeder (AddModulixModules).
        services.AddScoped<IModuleEntitlements, ModuleEntitlements>();
        services.AddScoped<AdministratorGuard>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<MembershipPermissions>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<ICompanyAdministration, CompanyAdministration>();
        services.AddScoped<IBranchAdministration, BranchAdministration>();
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ICustomFieldService>(sp => sp.GetRequiredService<CustomFieldService>());
        services.AddScoped<ICustomFieldProvider>(sp => sp.GetRequiredService<CustomFieldService>());
        services.AddScoped<ChoiceListService>();
        services.AddScoped<IChoiceListService>(sp => sp.GetRequiredService<ChoiceListService>());
        services.AddScoped<IChoiceLists>(sp => sp.GetRequiredService<ChoiceListService>());
        services.AddScoped<ISectorProfileService, SectorProfileService>();
        services.AddScoped<ICurrentAccess, CurrentAccess>();

        // Kullanıcı yönetimi (Y2); UserManager'ı host kaydeder (AddModulixAuth).
        services.AddScoped<UserAdministration>();
        services.AddScoped<IUserAdministration>(sp => sp.GetRequiredService<UserAdministration>());
        services.AddScoped<AccountLinks>();
        services.AddScoped<AccountAudit>();
        services.AddScoped<UserSessions>();
        services.AddScoped<AccountSecurity>();
        services.AddScoped<TwoFactorAccounts>();
        services.AddScoped<RecordHandover>();
        services.AddScoped<INotificationRecipients, NotificationRecipients>();

        // Platform yönetimi (Y4); ITenantScopeFactory'yi host kaydeder (tenant bağlamını o kurar).
        services.AddScoped<IPlatformAdministration, PlatformAdministration>();

        // Denetim kaydı okuma (Y5).
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ITenantSettings, TenantSettings>();
        services.AddScoped<ITenantLogo, TenantLogoStore>();
        services.AddScoped<TenantDisplayNames>();
        return services;
    }

    /// <summary>Platform veritabanının bekleyen migration'larını migration kullanıcısıyla uygular ('migrate' komutu).</summary>
    public static async Task MigratePlatformAsync(string platformMigrationsConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformMigrationsConnectionString);
        await using var context = CreatePlatformContext(platformMigrationsConnectionString);
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Tasarım zamanı ve 'migrate' için platform bağlamı. Tenant bağlamı gerektirmez.</summary>
    public static PlatformDbContext CreatePlatformContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        options.UsePlatformSqlServer(connectionString);
        return new PlatformDbContext(options.Options);
    }

    /// <summary>
    /// Tenant bağlamı olmayan tenant DbContext'i: tenant DB migration'ı ve 'dotnet ef migrations add' (tasarım zamanı)
    /// içindir. Belirli bir tenant DB'sine bağlanır; tenant satır kuralları yine çalışır ama aktif tenant yoktur.
    /// </summary>
    public static ModulixDbContext CreateContext(string connectionString, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var options = new DbContextOptionsBuilder<ModulixDbContext>();
        options.UseTenantSqlServer(connectionString);
        return new ModulixDbContext(options.Options, tenantAccessor: null, modules);
    }

    internal static DbContextOptionsBuilder UsePlatformSqlServer(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(
            connectionString,
            sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, PlatformHistorySchema));

    internal static DbContextOptionsBuilder UseTenantSqlServer(this DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, TenantHistorySchema))
            .ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>();
}
