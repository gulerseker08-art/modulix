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

internal sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> builder)
    {
        // Hazırlanmış dışa aktarma dosyası (ADR-0026, dilim 4): tenant verisi, RLS'li; içerik en çok MaxBytes, süresi dolunca silinir.
        builder.ToTable("ReportExports", DatabaseSchemas.Core);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.OwnerUserId).IsRequired();
        builder.Property(e => e.ReportName).HasMaxLength(ReportDefinition.NameMaxLength).IsRequired();
        builder.Property(e => e.DatasetKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.FileName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Content).HasColumnType("varbinary(max)").IsRequired();
        builder.Property(e => e.AccessStamp).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();

        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.HasIndex(e => new { e.TenantId, e.OwnerUserId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.ExpiresAt });
    }
}

internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> builder)
    {
        builder.ToTable("ReportSchedules", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.DayOfWeek).HasConversion<int?>();
        builder.Property(s => s.Format).HasConversion<string>().HasMaxLength(10).HasDefaultValue(ReportFileFormat.Csv);
        builder.Property(s => s.LastStatus).HasMaxLength(20);
        builder.Property(s => s.LastMessage).HasMaxLength(ReportSchedule.MessageMaxLength);
        builder.Property(s => s.StoppedReason).HasMaxLength(ReportSchedule.MessageMaxLength);
        builder.Property(s => s.NextRunAt).IsRequired();
        // Başarısız çalışmada yeniden deneme sayacı (en çok ReportSchedule.MaxAttempts); başarıyla sıfırlanır.
        builder.Property(s => s.Attempts).HasDefaultValue(0);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken();

        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
        builder.HasIndex(s => new { s.TenantId, s.OwnerUserId });
        // Vakti gelmiş zamanlamalar bu dizinle bulunur (yalnızca açık olanlar).
        builder.HasIndex(s => new { s.TenantId, s.NextRunAt }).HasFilter("[Enabled] = 1").HasDatabaseName("IX_ReportSchedules_TenantId_NextRunAt_Enabled");

        // Rapor silinince zamanlamaları da silinir.
        builder.HasOne<ReportDefinition>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.ReportId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReportScheduleRunConfiguration : IEntityTypeConfiguration<ReportScheduleRun>
{
    public void Configure(EntityTypeBuilder<ReportScheduleRun> builder)
    {
        builder.ToTable("ReportScheduleRuns", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Message).HasMaxLength(ReportSchedule.MessageMaxLength);
        builder.Property(r => r.Slot).IsRequired();
        builder.Property(r => r.StartedAt).IsRequired();

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        // Aynı planlı an için tek çalışma: yarışan ikinci örnek bu kısıta takılır (idempotency).
        builder.HasIndex(r => new { r.TenantId, r.ScheduleId, r.Slot }).IsUnique();

        builder.HasOne<ReportSchedule>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.ScheduleId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
