using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants", DatabaseSchemas.Platform);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(Tenant.SlugMaxLength).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Ignore(t => t.IsActive);
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("Memberships", DatabaseSchemas.Platform);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Status).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Ignore(m => m.IsActive);

        // Bir kullanıcı bir tenant'a yalnızca bir kez üye olur.
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        builder.HasIndex(m => m.UserId);

        // Kayıtlar silinmez; tenant kapatma ve üyelik devre dışı bırakma durum alanıyla yapılır.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModulixUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
