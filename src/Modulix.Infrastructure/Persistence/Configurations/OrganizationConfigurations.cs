using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Organization;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        // Tenant verisi (ADR-0008): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("Companies", DatabaseSchemas.Core);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(Company.NameMaxLength).IsRequired();
        builder.Property(c => c.IsDefault).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();

        // Tenant başına tek varsayılan şirket.
        builder.HasIndex(c => c.TenantId).IsUnique().HasDatabaseName("IX_Companies_TenantId_Default").HasFilter("[IsDefault] = 1");
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches", DatabaseSchemas.Core);
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.TenantId).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.HasIndex(b => new { b.TenantId, b.CompanyId, b.Name }).IsUnique();

        // Bileşik yabancı anahtar: şube ile şirket aynı tenant'ta olmak zorundadır. Şirket silinmeden önce şubeleri kaldırılır.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(b => new { b.TenantId, b.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MembershipCompanyConfiguration : IEntityTypeConfiguration<MembershipCompany>
{
    public void Configure(EntityTypeBuilder<MembershipCompany> builder)
    {
        // Üyeliğin şirket görevi (ADR-0008, adım 2): tenant verisidir; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("MembershipCompanies", DatabaseSchemas.Core);
        builder.HasKey(mc => mc.Id);
        builder.Property(mc => mc.Id).ValueGeneratedNever();
        builder.Property(mc => mc.TenantId).IsRequired();
        builder.Property(mc => mc.MembershipId).IsRequired();
        builder.Property(mc => mc.CompanyId).IsRequired();

        // Bileşik yabancı anahtar (ADR-0020 Dilim 2): görev ile üyelik aynı tenant'ta olmak zorundadır. Üyelik silinmeden önce
        // görevleri kaldırılır.
        builder.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(mc => new { mc.TenantId, mc.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(mc => mc.CreatedAt).IsRequired();

        // Geçici vekâlet (ADR-0008, adım 5): boş = süresiz görev; dolu = tarihli vekâlet.
        builder.Property(mc => mc.ValidFrom);
        builder.Property(mc => mc.ValidUntil);

        // Görevin idari varsayılan şubesi (ADR-0008, adım 5): boş = şube yok. Kapsam sınırı değildir.
        builder.Property(mc => mc.BranchId);

        // Aynı üyeliğe aynı şirket iki kez verilmez; erişilebilir şirket sorgusu bu üyelik üzerinden çalışır.
        builder.HasIndex(mc => new { mc.MembershipId, mc.CompanyId }).IsUnique();
        builder.HasIndex(mc => mc.CompanyId);

        // Bileşik yabancı anahtar: görev ile şirket aynı tenant'ta olmak zorundadır. Şirket silinmeden önce görevleri kaldırılır.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(mc => new { mc.TenantId, mc.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Varsayılan şube yabancı anahtarı (basit): şubenin aynı şirkete ait olduğu servis katmanında doğrulanır. Şube
        // silinmeden önce bu görevdeki varsayılanı kaldırılır.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(mc => mc.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
