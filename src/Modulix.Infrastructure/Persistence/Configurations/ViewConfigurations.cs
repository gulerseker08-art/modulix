using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Platform;
using Modulix.Core.Views;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class GridViewConfiguration : IEntityTypeConfiguration<GridView>
{
    public void Configure(EntityTypeBuilder<GridView> builder)
    {
        // Kişisel liste görünümü (ADR-0036): tenant verisi; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("GridViews", DatabaseSchemas.Core);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.TenantId).IsRequired();
        builder.Property(v => v.MembershipId).IsRequired();
        builder.Property(v => v.ScreenKey).HasMaxLength(GridView.ScreenKeyMaxLength).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(GridView.NameMaxLength).IsRequired();
        builder.Property(v => v.Columns).HasMaxLength(GridView.ColumnsMaxLength).IsRequired();
        builder.Property(v => v.Sort).HasMaxLength(GridView.SortMaxLength);
        builder.Property(v => v.Filters).HasMaxLength(GridView.FiltersMaxLength);
        builder.Property(v => v.Version).IsRequired().IsConcurrencyToken();
        builder.Property(v => v.CreatedAt).IsRequired();
        builder.Property(v => v.UpdatedAt).IsRequired();

        // Bileşik yabancı anahtar (ADR-0020): görünüm ile üyelik aynı tenant'ta olmak zorundadır.
        builder.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(v => new { v.TenantId, v.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Ad, üyenin aynı ekranındaki görünümler arasında tekildir.
        builder.HasIndex(v => new { v.TenantId, v.MembershipId, v.ScreenKey, v.Name }).IsUnique();
        builder.HasAlternateKey(v => new { v.TenantId, v.Id });
    }
}
