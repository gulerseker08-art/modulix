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

internal sealed class RegimeProfileConfiguration : IEntityTypeConfiguration<RegimeProfile>
{
    public void Configure(EntityTypeBuilder<RegimeProfile> builder)
    {
        // Platform kataloğu (ADR-0032 K16): TenantId taşımaz, RLS dışıdır.
        builder.ToTable("RegimeProfiles", DatabaseSchemas.Platform);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Code).HasMaxLength(RegimeProfile.CodeMaxLength).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(RegimeProfile.NameMaxLength).IsRequired();
        builder.HasIndex(p => p.Code).IsUnique();
    }
}

internal sealed class RegimeProfileVersionConfiguration : IEntityTypeConfiguration<RegimeProfileVersion>
{
    public void Configure(EntityTypeBuilder<RegimeProfileVersion> builder)
    {
        builder.ToTable("RegimeProfileVersions", DatabaseSchemas.Platform);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.ContentJson).IsRequired();
        builder.Property(v => v.VerificationNote).HasMaxLength(RegimeProfileVersion.NoteMaxLength);
        builder.HasOne<RegimeProfile>().WithMany().HasForeignKey(v => v.ProfileId).OnDelete(DeleteBehavior.Restrict);

        // Profil başına sürüm numarası benzersizdir; en çok bir taslak ve bir yayın olabilir (filtreli benzersiz dizin).
        builder.HasIndex(v => new { v.ProfileId, v.Number }).IsUnique();
        // Aynı sütunda iki dizin olduğundan adlandırılmış dizin kullanılır (EF aksi halde ikisini birleştirir).
        builder.HasIndex(v => v.ProfileId, "UX_RegimeProfileVersions_Draft").IsUnique()
            .HasFilter($"[State] = {(int)RegimeProfileVersionState.Draft}");
        builder.HasIndex(v => v.ProfileId, "UX_RegimeProfileVersions_Published").IsUnique()
            .HasFilter($"[State] = {(int)RegimeProfileVersionState.Published}");
    }
}
