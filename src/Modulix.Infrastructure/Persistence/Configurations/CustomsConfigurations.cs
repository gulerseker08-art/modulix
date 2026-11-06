using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class GtipCatalogVersionConfiguration : IEntityTypeConfiguration<GtipCatalogVersion>
{
    public void Configure(EntityTypeBuilder<GtipCatalogVersion> builder)
    {
        // Platform kataloğu (ADR-0032 K9): TenantId taşımaz, RLS dışıdır.
        builder.ToTable("GtipCatalogVersions", DatabaseSchemas.Platform);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).HasMaxLength(GtipCatalogRules.NameMaxLength).IsRequired();
        builder.Property(v => v.EffectiveFrom).HasColumnType("date");
        builder.Property(v => v.SourceFileName).HasMaxLength(GtipCatalogRules.FileNameMaxLength);

        // Bir tarihte tek sürüm yürürlüktedir; aynı tarihli ikinci sürüm hangisinin geçerli olduğunu belirsiz bırakırdı.
        builder.HasIndex(v => v.EffectiveFrom).IsUnique();
    }
}

internal sealed class GtipCatalogEntryConfiguration : IEntityTypeConfiguration<GtipCatalogEntry>
{
    public void Configure(EntityTypeBuilder<GtipCatalogEntry> builder)
    {
        builder.ToTable("GtipCatalogEntries", DatabaseSchemas.Platform);

        // (Sürüm, kod) anahtarı hem tekilliği hem de kod ve kod ön eki aramasını (LIKE 'xxxx%') karşılar.
        builder.HasKey(e => new { e.VersionId, e.Code });
        builder.Property(e => e.Code).HasMaxLength(GtipRules.MaxLength).IsUnicode(false);
        builder.Property(e => e.Description).HasMaxLength(GtipCatalogRules.DescriptionMaxLength).IsRequired();
        builder.Property(e => e.Unit).HasMaxLength(GtipCatalogRules.UnitMaxLength);
        builder.HasOne<GtipCatalogVersion>().WithMany().HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Cascade);
    }
}
