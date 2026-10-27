using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Platform (merkezi) veritabanının DbContext'i (ADR-0009): tenant kayıtları ve yaşam döngüsü, ortak kullanıcı
/// kimlikleri (Identity), kullanıcı–tenant üyelikleri, platform yöneticileri, modül erişimleri ve açık oturumlar.
/// Tenant iş verisi burada <b>değildir</b>; o <see cref="ModulixDbContext"/> ile tenant başına ayrı veritabanındadır.
///
/// Tenant DB'si ile platform DB'si arasında cross-database foreign key veya join yoktur: tenant tablolarındaki
/// <c>MembershipId</c> düz bir <c>Guid</c>'dir ve bu bağlamdaki üyelikle yalnızca mantıksal olarak ilişkilidir.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : IdentityUserContext<ModulixUser, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<ModuleEntitlement> ModuleEntitlements => Set<ModuleEntitlement>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<QueuedEmail> EmailMessages => Set<QueuedEmail>();

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

        // Yalnızca platform entity yapılandırmaları (tenant yapılandırmaları ModulixDbContext'e aittir).
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PlatformDbContext).Assembly,
            type => typeof(IPlatformConfiguration).IsAssignableFrom(type));
    }
}

/// <summary>
/// Platform veritabanına ait bir entity yapılandırmasını işaretler (ADR-0009). İki bağlam aynı derlemeyi tarar;
/// bu işaret platform tablolarının tenant bağlamına, tenant tablolarının platform bağlamına sızmasını engeller.
/// </summary>
public interface IPlatformConfiguration;
