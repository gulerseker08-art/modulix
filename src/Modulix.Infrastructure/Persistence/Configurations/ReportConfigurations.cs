using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Reporting;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ReportDefinitionConfiguration : IEntityTypeConfiguration<ReportDefinition>
{
    public void Configure(EntityTypeBuilder<ReportDefinition> builder)
    {
        // Tenant verisi (ADR-0026, dilim 3): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("Reports", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.OwnerUserId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(ReportDefinition.NameMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(ReportDefinition.DescriptionMaxLength);
        builder.Property(r => r.DatasetKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.SpecJson).IsRequired();
        builder.Property(r => r.Visibility).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();
        builder.Property(r => r.Version).IsConcurrencyToken();

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        // Ad kullanıcı içinde tekildir (benzersizlik tenant ile birlikte).
        builder.HasIndex(r => new { r.TenantId, r.OwnerUserId, r.Name }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.Visibility });
    }
}

internal sealed class ReportShareConfiguration : IEntityTypeConfiguration<ReportShare>
{
    public void Configure(EntityTypeBuilder<ReportShare> builder)
    {
        builder.ToTable("ReportShares", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
        builder.HasIndex(s => new { s.TenantId, s.ReportId, s.UserId }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.UserId });

        // Bileşik yabancı anahtar (ADR-0020): paylaşım ile rapor aynı tenant'ta olmak zorundadır; rapor silinince paylaşımlar da silinir.
        builder.HasOne<ReportDefinition>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ReportId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReportFavoriteConfiguration : IEntityTypeConfiguration<ReportFavorite>
{
    public void Configure(EntityTypeBuilder<ReportFavorite> builder)
    {
        builder.ToTable("ReportFavorites", DatabaseSchemas.Core);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
        builder.HasIndex(f => new { f.TenantId, f.ReportId, f.UserId }).IsUnique();
        builder.HasIndex(f => new { f.TenantId, f.UserId });

        builder.HasOne<ReportDefinition>()
            .WithMany()
            .HasForeignKey(f => new { f.TenantId, f.ReportId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
