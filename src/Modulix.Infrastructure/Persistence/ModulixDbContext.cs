using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Communication;
using Modulix.Core.Customization;
using Modulix.Core.Customs;
using Modulix.Core.Imports;
using Modulix.Core.Lookups;
using Modulix.Core.Teams;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Reporting;
using Modulix.Core.FieldRules;
using Modulix.Core.Sectors;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Tek paylaşımlı veritabanının DbContext'i (ADR-0002, ADR-0020): platform tabloları (kimlik, tenant kayıtları,
/// üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu; <c>platform</c> şeması, RLS dışı) ve bütün tenant'ların iş
/// verisi (<c>TenantId</c> taşır) aynı bağlamdadır. Modüller bu sınıfa erişemez; kayıtlara yalnızca kayıt boru hattı
/// üzerinden ulaşır (talimat Bölüm 3, kural 1).
///
/// Tenant izolasyonu, birinci katman (ADR-0002): <see cref="ITenantEntity"/> uygulayan her entity'ye otomatik sorgu
/// filtresi uygulanır ve kaydederken TenantId aktif tenant'tan atanır. İkinci katman veritabanındadır
/// (<see cref="TenantIsolationSql"/>); filtre unutulsa bile veri sızmaz.
///
/// Modül tabloları: kayıtlı her modülün projesindeki <c>IEntityTypeConfiguration</c> sınıfları modele eklenir
/// (<see cref="ApplyModuleModels"/>). Modüller bu tablolara <see cref="IModuleData"/> üzerinden erişir.
/// </summary>
public class ModulixDbContext(
    DbContextOptions<ModulixDbContext> options,
    ITenantContextAccessor? tenantAccessor = null,
    ModuleCatalog? modules = null)
    : IdentityUserContext<ModulixUser, Guid>(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(ModulixDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Platform tabloları (RLS dışı).
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<ModuleEntitlement> ModuleEntitlements => Set<ModuleEntitlement>();

    /// <summary>Platform işlemlerinin tenant'lar arası, ekleme-yalnız kaydı (ADR-0028).</summary>
    public DbSet<PlatformAuditEntry> PlatformAuditLog => Set<PlatformAuditEntry>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();

    public DbSet<LimitOverride> LimitOverrides => Set<LimitOverride>();

    public DbSet<SupportAccessGrant> SupportAccessGrants => Set<SupportAccessGrant>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<QueuedEmail> EmailMessages => Set<QueuedEmail>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<EventDelivery> EventDeliveries => Set<EventDelivery>();

    // Tenant tabloları (TenantId + RLS).
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    public DbSet<TenantLogo> TenantLogos => Set<TenantLogo>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    // Ortak iletişim tercihi (ADR-0025).
    public DbSet<CommunicationPreference> CommunicationPreferences => Set<CommunicationPreference>();

    public DbSet<ConsentEvent> ConsentEvents => Set<ConsentEvent>();

    public DbSet<CommunicationSuppression> CommunicationSuppressions => Set<CommunicationSuppression>();

    public DbSet<CommunicationTopic> CommunicationTopics => Set<CommunicationTopic>();

    public DbSet<CommunicationTopicPreference> CommunicationTopicPreferences => Set<CommunicationTopicPreference>();

    public DbSet<FrequencyWindow> FrequencyWindows => Set<FrequencyWindow>();

    public DbSet<CommunicationLinkToken> CommunicationLinkTokens => Set<CommunicationLinkToken>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();

    public DbSet<BusinessUnitMember> BusinessUnitMembers => Set<BusinessUnitMember>();

    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();

    public DbSet<AutomationRuleVersion> AutomationRuleVersions => Set<AutomationRuleVersion>();

    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();

    public DbSet<AutomationRunStep> AutomationRunSteps => Set<AutomationRunStep>();

    public DbSet<AutomationScanCursor> AutomationScanCursors => Set<AutomationScanCursor>();

    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();

    public DbSet<ImportRowIssue> ImportRowIssues => Set<ImportRowIssue>();

    public DbSet<ReportDefinition> Reports => Set<ReportDefinition>();

    public DbSet<ReportShare> ReportShares => Set<ReportShare>();

    public DbSet<ReportFavorite> ReportFavorites => Set<ReportFavorite>();

    public DbSet<ReportExport> ReportExports => Set<ReportExport>();

    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();

    public DbSet<ReportScheduleRun> ReportScheduleRuns => Set<ReportScheduleRun>();

    public DbSet<BusinessUnitRole> BusinessUnitRoles => Set<BusinessUnitRole>();

    public DbSet<RoleScopeOverride> RoleScopeOverrides => Set<RoleScopeOverride>();

    public DbSet<TenantScopeLimit> TenantScopeLimits => Set<TenantScopeLimit>();

    public DbSet<MembershipCompany> MembershipCompanies => Set<MembershipCompany>();

    public DbSet<CustomField> CustomFields => Set<CustomField>();

    public DbSet<TenantChoiceList> ChoiceLists => Set<TenantChoiceList>();

    public DbSet<TenantLookupSetting> LookupSettings => Set<TenantLookupSetting>();

    public DbSet<TenantFormLayout> FormLayouts => Set<TenantFormLayout>();

    public DbSet<TenantImportDesign> ImportDesigns => Set<TenantImportDesign>();

    /// <summary>Kullanıcının kişisel liste görünümleri (ADR-0034).</summary>
    public DbSet<Modulix.Core.Views.GridView> GridViews => Set<Modulix.Core.Views.GridView>();

    public DbSet<ActivityNode> ActivityNodes => Set<ActivityNode>();

    public DbSet<Capability> Capabilities => Set<Capability>();

    public DbSet<CapabilityDependency> CapabilityDependencies => Set<CapabilityDependency>();

    public DbSet<ActivityNodeCapability> ActivityNodeCapabilities => Set<ActivityNodeCapability>();

    public DbSet<TenantActivitySelection> ActivitySelections => Set<TenantActivitySelection>();

    public DbSet<RelationshipRole> RelationshipRoles => Set<RelationshipRole>();

    public DbSet<GtipCatalogVersion> GtipCatalogVersions => Set<GtipCatalogVersion>();

    public DbSet<GtipCatalogEntry> GtipCatalogEntries => Set<GtipCatalogEntry>();

    public DbSet<RulePackage> RulePackages => Set<RulePackage>();
    public DbSet<RulePackageVersion> RulePackageVersions => Set<RulePackageVersion>();
    public DbSet<TenantRuleAdaptation> RuleAdaptations => Set<TenantRuleAdaptation>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>Sorgu filtresinde kullanılır; EF her sorguda yeniden okur. Aktif tenant yoksa hiçbir satır dönmez.</summary>
    private Guid? CurrentTenantId => tenantAccessor?.Current?.TenantId;

    /// <summary>Modeli belirleyen modüller; EF model önbelleğinin anahtarıdır (<see cref="ModuleModelCacheKeyFactory"/>).</summary>
    internal string ModelKey => modules is null ? string.Empty : string.Join(',', modules.Modules.Select(m => m.Key));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Kimlik tabloları platform geneli kayıtlardır.
        modelBuilder.Entity<ModulixUser>(user =>
        {
            user.ToTable("Users", DatabaseSchemas.Platform);
            user.Property(u => u.DisplayName).HasMaxLength(ModulixUser.DisplayNameMaxLength).IsRequired();
            user.Property(u => u.PreferredLanguage).HasMaxLength(LanguageOptions.MaxLength);
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims", DatabaseSchemas.Platform);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins", DatabaseSchemas.Platform);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens", DatabaseSchemas.Platform);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ModulixDbContext).Assembly);

        // Özel alan filtreleri (Y9a): modüller sorguda JsonFunctions.Value kullanır.
        modelBuilder.HasDbFunction(typeof(JsonFunctions).GetMethod(nameof(JsonFunctions.Value))!)
            .HasName("JSON_VALUE")
            .IsBuiltIn();
        ApplyModuleModels(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && t.BaseType is null)
                     .ToList())
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }

        // Outbox ve teslimler ITenantEntity değildir (RLS dışı; işleyici tenant bağlamını açıkça kurar) ama tek veritabanında
        // (ADR-0020) her tenant bağlamı yalnızca kendi olaylarını görmelidir: ADR-0009'daki fiziksel ayrımın yerini bu
        // filtre alır. Aktif tenant yoksa hiçbir olay görünmez.
        modelBuilder.Entity<OutboxMessage>().HasQueryFilter(o => o.TenantId == CurrentTenantId);
        modelBuilder.Entity<EventDelivery>().HasQueryFilter(d => d.TenantId == CurrentTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Modül entity'lerini modele ekler. Bir modülün tablosu tenant verisi değilse veya başka şemadaysa
    /// uygulama açılmaz: her modül yalnızca kendi şemasına, yalnızca tenant'a ait kayıt yazar.
    /// </summary>
    private void ApplyModuleModels(ModelBuilder modelBuilder)
    {
        foreach (var module in modules?.Modules ?? [])
        {
            var assembly = module.GetType().Assembly;
            if (assembly == typeof(ModulixDbContext).Assembly || assembly == typeof(ModuleCatalog).Assembly)
            {
                continue;
            }

            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => t.ClrType.Assembly == assembly))
            {
                if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
                {
                    throw new InvalidOperationException(
                        $"{module.Key} modülünün entity'si {entityType.ClrType.Name} ITenantEntity uygulamıyor.");
                }

                if (entityType.GetSchema() != module.Key)
                {
                    throw new InvalidOperationException(
                        $"{module.Key} modülünün tablosu {entityType.GetTableName()} '{entityType.GetSchema()}' şemasında; '{module.Key}' olmalı.");
                }
            }
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == CurrentTenantId);
    }

    /// <summary>
    /// Yeni tenant kaydına aktif tenant'ı yazar; aktif tenant yoksa, başka tenant'a yazılmak
    /// isteniyorsa veya mevcut kaydın TenantId'si değiştirilmek isteniyorsa kaydetmeyi durdurur.
    /// </summary>
    private void ApplyTenantRules()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var current = CurrentTenantId
                ?? throw new InvalidOperationException(
                    $"Aktif tenant olmadan tenant kaydı yazılamaz ({entry.Metadata.ClrType.Name}).");

            var tenantProperty = entry.Property(nameof(ITenantEntity.TenantId));

            if (entry.State == EntityState.Added)
            {
                var assigned = (Guid)tenantProperty.CurrentValue!;
                if (assigned != Guid.Empty && assigned != current)
                {
                    throw new InvalidOperationException(
                        $"Başka bir tenant'a kayıt yazılamaz ({entry.Metadata.ClrType.Name}).");
                }

                tenantProperty.CurrentValue = current;
            }
            else if (tenantProperty.IsModified || (Guid)tenantProperty.OriginalValue! != current)
            {
                throw new InvalidOperationException(
                    $"Kaydın tenant'ı değiştirilemez ({entry.Metadata.ClrType.Name}).");
            }
        }
    }
}
