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
using Modulix.Core.Lookups;
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
using Modulix.Infrastructure.Lookups;
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
    /// <summary>Uygulama bağlantısı (modulix_app): veri okur/yazar, şema değiştiremez.</summary>
    public const string AppConnectionName = "Modulix";

    /// <summary>Migration bağlantısı (modulix_migrator): yalnızca 'migrate' komutu kullanır.</summary>
    public const string MigrationsConnectionName = "ModulixMigrations";

    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// İki sınırlı bağlam aynı veritabanındadır (ADR-0020, Dilim 1); migration geçmişleri ayrı şemalardaki tablolarda
    /// tutulur ki zincirler birbirine karışmasın. Dilim 2'de bağlamlar ve zincirler birleşir.
    /// </summary>
    private const string PlatformHistorySchema = DatabaseSchemas.Platform;

    private const string TenantHistorySchema = DatabaseSchemas.Core;

    public static IServiceCollection AddModulixPersistence(
        this IServiceCollection services, string appConnectionString, string migrationsConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);

        services.AddMemoryCache();
        services.AddSingleton(new ModulixDatabaseOptions(appConnectionString, migrationsConnectionString));

        // Platform bağlamı: platform şeması (RLS dışı).
        services.AddDbContext<PlatformDbContext>(options => options.UsePlatformSqlServer(appConnectionString));

        // Tenant bağlamı: aynı veritabanı; interceptor isteğin aktif tenant'ını oturuma yazar, Row-Level Security onu
        // uygular (ADR-0002). Aktif tenant yoksa hiçbir tenant satırı görünmez ve yazılamaz.
        services.AddDbContext<ModulixDbContext>((serviceProvider, options) =>
        {
            var tenantAccessor = serviceProvider.GetService<ITenantContextAccessor>();
            options.UseTenantSqlServer(appConnectionString)
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
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<ICompanyAdministration, CompanyAdministration>();
        services.AddScoped<IBranchAdministration, BranchAdministration>();
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ICustomFieldService>(sp => sp.GetRequiredService<CustomFieldService>());
        services.AddScoped<ICustomFieldProvider>(sp => sp.GetRequiredService<CustomFieldService>());
        services.AddScoped<ChoiceListService>();
        services.AddScoped<IChoiceListService>(sp => sp.GetRequiredService<ChoiceListService>());
        services.AddScoped<IChoiceLists>(sp => sp.GetRequiredService<ChoiceListService>());
        services.AddScoped<LookupService>();
        services.AddScoped<ILookupSettings>(sp => sp.GetRequiredService<LookupService>());
        services.AddScoped<ILookupSearch>(sp => sp.GetRequiredService<LookupService>());
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

    /// <summary>
    /// Bekleyen migration'ları migration kullanıcısıyla uygular ('migrate' komutu): önce platform zinciri, sonra tenant
    /// zinciri; ikisi de aynı veritabanına gider (ADR-0020). Tek veritabanı bütün tenant'ları etkilediği için
    /// migration'lar geriye uyumlu ve kesintisiz yazılır.
    /// </summary>
    public static async Task MigrateAsync(string migrationsConnectionString, ModuleCatalog modules, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);
        ArgumentNullException.ThrowIfNull(modules);

        await using (var platform = CreatePlatformContext(migrationsConnectionString))
        {
            await platform.Database.MigrateAsync(cancellationToken);
        }

        await using (var tenant = CreateContext(migrationsConnectionString, modules))
        {
            await tenant.Database.MigrateAsync(cancellationToken);
        }
    }

    /// <summary>Tasarım zamanı ve 'migrate' için platform bağlamı. Tenant bağlamı gerektirmez.</summary>
    public static PlatformDbContext CreatePlatformContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        options.UsePlatformSqlServer(connectionString);
        return new PlatformDbContext(options.Options);
    }

    /// <summary>
    /// Aktif tenant'ı olmayan tenant DbContext'i: 'migrate' komutu ve 'dotnet ef migrations add' (tasarım zamanı)
    /// içindir. Tenant satır kuralları yine çalışır ama aktif tenant yoktur; veri okumak için kullanılmaz.
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
