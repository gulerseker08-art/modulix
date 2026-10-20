using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Core ve platform tablolarının DbContext'i. Modüller bu sınıfa erişemez; kayıtlara
/// yalnızca kayıt boru hattı üzerinden ulaşır (talimat Bölüm 3, kural 1).
/// Kimlik tabloları rolsüzdür: roller tenant'a özeldir ve üyeliğe bağlanır (Görev E4).
/// </summary>
public class ModulixDbContext(DbContextOptions<ModulixDbContext> options)
    : IdentityUserContext<ModulixUser, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

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
    }
}
