using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Imports;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ImportRunConfiguration : IEntityTypeConfiguration<ImportRun>
{
    public void Configure(EntityTypeBuilder<ImportRun> builder)
    {
        builder.ToTable("ImportRuns", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.EntityKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.FileName).HasMaxLength(ImportRun.FileNameMaxLength).IsRequired();
        builder.Property(r => r.FileHash).HasMaxLength(ImportRun.FileHashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.Mode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Policy).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.CorrelationId).HasMaxLength(ImportRun.CorrelationMaxLength);
        builder.Property(r => r.Note).HasMaxLength(ImportRun.NoteMaxLength);

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        // Günlük ekranı: en yeni üstte; kullanıcıya, duruma ve kayıt türüne göre süzülür.
        builder.HasIndex(r => new { r.TenantId, r.CreatedAt });
        builder.HasIndex(r => new { r.TenantId, r.UserId, r.CreatedAt });
        builder.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.TenantId, r.EntityKey, r.CreatedAt });

        // Aynı dosya uyarısı ve "hepsi ya da hiç" için kuru çalışma eşlemesi dosya özetiyle aranır.
        builder.HasIndex(r => new { r.TenantId, r.FileHash, r.CreatedAt });
    }
}

internal sealed class ImportRowIssueConfiguration : IEntityTypeConfiguration<ImportRowIssue>
{
    public void Configure(EntityTypeBuilder<ImportRowIssue> builder)
    {
        builder.ToTable("ImportRowIssues", DatabaseSchemas.Core);
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.ColumnKey).HasMaxLength(ImportRowIssue.ColumnKeyMaxLength);
        builder.Property(i => i.Code).HasMaxLength(ImportRowIssue.CodeMaxLength).IsRequired();
        builder.Property(i => i.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.Message).HasMaxLength(ImportRowIssue.MessageMaxLength).IsRequired();

        // Koşunun hata listesi satır sırasıyla okunur; hata kodu özeti ve saklama temizliği de buradan geçer.
        builder.HasIndex(i => new { i.TenantId, i.RunId, i.RowNo });
        builder.HasIndex(i => new { i.TenantId, i.RunId, i.Code });
        builder.HasIndex(i => new { i.TenantId, i.CreatedAt });

        // Hata, bağlı olduğu koşuyla aynı tenant'tadır; koşu silinmeden önce hataları temizlenir.
        builder.HasOne<ImportRun>()
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
