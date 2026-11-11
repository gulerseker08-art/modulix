using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Files;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        // Kayda bağlı dosyanın üst verisi (ADR-0040): tenant verisi; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("StoredFiles", DatabaseSchemas.Core);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.EntityKey).HasMaxLength(StoredFile.EntityKeyMaxLength).IsRequired();
        builder.Property(f => f.RecordId).IsRequired();
        builder.Property(f => f.FileName).HasMaxLength(StoredFile.FileNameMaxLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(StoredFile.ContentTypeMaxLength).IsRequired();
        builder.Property(f => f.SizeBytes).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(f => f.StorageKey).HasMaxLength(StoredFile.StorageKeyLength).IsRequired();
        builder.Property(f => f.Classification).HasMaxLength(StoredFile.ClassificationMaxLength).IsRequired();
        builder.Property(f => f.ScanStatus).HasMaxLength(StoredFile.ScanStatusMaxLength).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.Version).IsRequired().IsConcurrencyToken();

        // Bir kaydın dosyaları; liste ve kayıt başına sınır sayımı bu dizini kullanır.
        builder.HasIndex(f => new { f.TenantId, f.EntityKey, f.RecordId });

        // Nesne anahtarı tenant içinde tekildir (rastgele; çakışma olursa yazma başarısız olur).
        builder.HasIndex(f => new { f.TenantId, f.StorageKey }).IsUnique();
        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
    }
}
