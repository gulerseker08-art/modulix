using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Authorization;
using Modulix.Core.Teams;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Core ve platform tablolarının DbContext'i. Modüller bu sınıfa erişemez; kayıtlara
/// yalnızca kayıt boru hattı üzerinden ulaşır (talimat Bölüm 3, kural 1).
/// Kimlik tabloları rolsüzdür: roller tenant'a özeldir ve üyeliğe bağlanır (<see cref="Role"/>, <see cref="MembershipRole"/>).
///
/// Tenant izolasyonu, birinci katman (ADR-0002): <see cref="ITenantEntity"/> uygulayan her
/// entity'ye otomatik sorgu filtresi uygulanır ve kaydederken TenantId aktif tenant'tan atanır.
/// İkinci katman veritabanındadır (<see cref="TenantIsolationSql"/>).
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

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    public DbSet<TenantLogo> TenantLogos => Set<TenantLogo>();

    public DbSet<ModuleEntitlement> ModuleEntitlements => Set<ModuleEntitlement>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<QueuedEmail> EmailMessages => Set<QueuedEmail>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

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
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims", DatabaseSchemas.Platform);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins", DatabaseSchemas.Platform);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens", DatabaseSchemas.Platform);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ModulixDbContext).Assembly);
        ApplyModuleModels(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && t.BaseType is null)
                     .ToList())
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
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
