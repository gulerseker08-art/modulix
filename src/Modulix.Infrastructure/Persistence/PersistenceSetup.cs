using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Audit;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Sectors;
using Modulix.Core.Lookups;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Core.Reporting;
using Modulix.Core.Settings;
using Modulix.Core.Teams;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Communication;
using Modulix.Infrastructure.Customization;
using Modulix.Infrastructure.Sectors;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Lookups;
using Modulix.Infrastructure.Modules;
using Modulix.Infrastructure.Organization;
using Modulix.Infrastructure.Platform;
using Modulix.Infrastructure.Reporting;
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

    /// <summary>Tek migration zinciri (ADR-0020, Dilim 2); geçmiş tablosu platform şemasındadır.</summary>
    private const string MigrationsHistorySchema = DatabaseSchemas.Platform;

    public static IServiceCollection AddModulixPersistence(
        this IServiceCollection services, string appConnectionString, string migrationsConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);

        services.AddMemoryCache();
        services.AddSingleton(new ModulixDatabaseOptions(appConnectionString, migrationsConnectionString));

        // Tek bağlam (ADR-0020): platform tabloları ve bütün tenant'ların iş verisi aynı veritabanında. Interceptor isteğin aktif
        // tenant'ını oturuma yazar, Row-Level Security onu uygular (ADR-0002). Aktif tenant yoksa hiçbir tenant satırı görünmez
        // ve yazılamaz; platform tabloları (kimlik, üyelik, tenant kayıtları) tenant bağlamı olmadan da okunur.
        services.AddDbContext<ModulixDbContext>((serviceProvider, options) =>
        {
            var tenantAccessor = serviceProvider.GetService<ITenantContextAccessor>();
            options.UseModulixSqlServer(appConnectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenantAccessor));
        });

        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<ITenantMembers, TenantMembers>();
        services.AddScoped<ITenantDataTransfer, TenantDataTransfer>();
        services.AddScoped<IModuleData, ModuleData>();
        services.AddScoped<ITransactionLock, SqlTransactionLock>();

        // Modül hakları ve izinler; ModuleCatalog'u host kaydeder (AddModulixModules).
        services.AddScoped<IModuleEntitlements, ModuleEntitlements>();
        services.AddScoped<IModuleLicense, ModuleLicense>();
        services.AddScoped<IEntitlementResolver, Modulix.Infrastructure.Platform.EntitlementResolver>();
        services.AddScoped<IPlanAdministration, Modulix.Infrastructure.Platform.PlanAdministration>();
        services.AddScoped<IPlatformTeam, Modulix.Infrastructure.Platform.PlatformTeam>();
        services.AddScoped<AdministratorGuard>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<MembershipPermissions>();
        services.AddScoped<ITeamService, TeamService>();

        // Ortak iletişim tercihi (ADR-0025): modüller okuma sözleşmesini, API yönetimi kullanır.
        services.AddScoped<CommunicationService>();
        services.AddScoped<ICommunicationPreferences>(sp => sp.GetRequiredService<CommunicationService>());
        services.AddScoped<ICommunicationAdministration>(sp => sp.GetRequiredService<CommunicationService>());
        services.AddScoped<ICommunicationTopics>(sp => sp.GetRequiredService<CommunicationService>());
        services.AddScoped<IFrequencyLimiter, FrequencyLimiter>();
        services.AddScoped<IUnsubscribeLinks, UnsubscribeLinks>();
        services.AddScoped<CommunicationCleanup>();
        services.AddScoped<IOptOutRecorder, OptOutRecorder>();
        services.AddScoped<ICommunicationPortal, CommunicationPortal>();
        services.AddScoped<IJournalWriter, JournalWriter>();
        services.AddScoped<ICompanyAdministration, CompanyAdministration>();
        services.AddScoped<IBranchAdministration, BranchAdministration>();
        services.AddScoped<IBusinessUnitService, BusinessUnitService>();
        services.AddScoped<IMembershipScope, MembershipScope>();
        services.AddScoped<IAutomationRuleService, AutomationRuleService>();
        services.AddScoped<IAutomationRunStore, AutomationRunStore>();
        services.AddScoped<IAutomationRunService, AutomationRunService>();
        services.AddSingleton<AutomationRunner>();

        // Ortak rapor merkezi (ADR-0026, dilim 3): veri kümeleri modüllerden gelir, okumalar kayıt boru hattından geçer.
        services.AddScoped<ReportColumnResolver>();
        services.AddScoped<IReportRunner, ReportRunner>();
        services.AddScoped<IReportCatalog, ReportCatalogService>();
        services.AddScoped<IReportCenter, ReportCenter>();
        services.AddScoped<ReportAccessStamp>();
        services.AddScoped<IReportExports, ReportExports>();
        services.AddScoped<IReportSchedules, ReportSchedules>();
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
        services.AddScoped<ISectorCatalog, SectorCatalogService>();
        services.AddScoped<ITenantActivitySelection, TenantActivitySelectionService>();
        services.AddScoped<FormLayoutService>();
        services.AddScoped<IFormLayoutService>(sp => sp.GetRequiredService<FormLayoutService>());
        services.AddScoped<IFormLayoutProvider>(sp => sp.GetRequiredService<FormLayoutService>());
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
        services.AddScoped<ITenantClock, TenantClock>();
        services.AddScoped<ITenantLogo, TenantLogoStore>();
        services.AddScoped<TenantDisplayNames>();
        return services;
    }

    /// <summary>
    /// Bekleyen migration'ları migration kullanıcısıyla uygular ('migrate' komutu). Tek veritabanı bütün tenant'ları
    /// etkilediği için migration'lar geriye uyumlu ve kesintisiz yazılır (ADR-0020).
    /// </summary>
    public static async Task MigrateAsync(string migrationsConnectionString, ModuleCatalog modules, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsConnectionString);
        ArgumentNullException.ThrowIfNull(modules);

        await using var context = CreateContext(migrationsConnectionString, modules);
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Aktif tenant'ı olmayan DbContext: 'migrate' komutu ve 'dotnet ef migrations add' (tasarım zamanı)
    /// içindir. Tenant satır kuralları yine çalışır ama aktif tenant yoktur; veri okumak için kullanılmaz.
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
                sql => sql.MigrationsHistoryTable(MigrationsHistoryTable, MigrationsHistorySchema))
            .ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>();
}
