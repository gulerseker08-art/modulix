using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Credentials;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        // Süreli belge (ADR-0038 K3): tenant verisi; Row-Level Security ile korunur (migration: TenantIsolationSql). Sahip kayda veritabanı
        // yabancı anahtarı kurulmaz: sahip başka modülün tablosundadır, bağ kayıt deposunda denetlenir.
        builder.ToTable("Credentials", DatabaseSchemas.Core);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.OwnerEntityKey).HasMaxLength(Credential.OwnerEntityKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.OwnerId).IsRequired();
        builder.Property(c => c.TypeKey).HasMaxLength(Credential.TypeKeyMaxLength).IsRequired();
        builder.Property(c => c.DocumentNo).HasMaxLength(Credential.DocumentNoMaxLength);
        builder.Property(c => c.IssuedBy).HasMaxLength(Credential.IssuedByMaxLength);
        builder.Property(c => c.Status).HasMaxLength(20).IsUnicode(false).IsRequired().HasDefaultValue(CredentialStatuses.Active);
        builder.Property(c => c.Notes).HasMaxLength(Credential.NotesMaxLength);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
        builder.Property(c => c.Version).IsConcurrencyToken();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        // Bir kaydın belgeleri (kart sekmesi) ve bitişi yaklaşan belgeler (uyarı taraması, liste).
        builder.HasIndex(c => new { c.TenantId, c.OwnerEntityKey, c.OwnerId }).HasDatabaseName("IX_Credentials_Owner");
        builder.HasIndex(c => new { c.TenantId, c.ValidTo }).HasDatabaseName("IX_Credentials_ValidTo");

        // Belge numarası verilirse aynı sahipte aynı türde tekildir (ADR-0038 tablo: TenantId, sahip, tür, no).
        builder.HasIndex(c => new { c.TenantId, c.OwnerEntityKey, c.OwnerId, c.TypeKey, c.DocumentNo }).IsUnique()
            .HasFilter("[DocumentNo] IS NOT NULL").HasDatabaseName("UX_Credentials_DocumentNo");
    }
}
