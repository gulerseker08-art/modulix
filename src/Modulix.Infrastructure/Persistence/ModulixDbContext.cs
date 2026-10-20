using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Core ve platform tablolarının DbContext'i. Modüller bu sınıfa erişemez; kayıtlara
/// yalnızca kayıt boru hattı üzerinden ulaşır (talimat Bölüm 3, kural 1).
/// Kimlik tabloları rolsüzdür: roller tenant'a özeldir ve üyeliğe bağlanır (Görev E4).
///
/// Tenant izolasyonu, birinci katman (ADR-0002): <see cref="ITenantEntity"/> uygulayan her
/// entity'ye otomatik sorgu filtresi uygulanır ve kaydederken TenantId aktif tenant'tan atanır.
/// İkinci katman veritabanındadır (<see cref="TenantIsolationSql"/>).
/// </summary>
public class ModulixDbContext(DbContextOptions<ModulixDbContext> options, ITenantContextAccessor? tenantAccessor = null)
    : IdentityUserContext<ModulixUser, Guid>(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(ModulixDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    /// <summary>Sorgu filtresinde kullanılır; EF her sorguda yeniden okur. Aktif tenant yoksa hiçbir satır dönmez.</summary>
    private Guid? CurrentTenantId => tenantAccessor?.Current?.TenantId;

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
