using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Communication;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class CommunicationPreferenceConfiguration : IEntityTypeConfiguration<CommunicationPreference>
{
    public void Configure(EntityTypeBuilder<CommunicationPreference> builder)
    {
        // Ortak iletişim tercihi (ADR-0025): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("CommunicationPreferences", DatabaseSchemas.Core);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Address).HasMaxLength(CommunicationPreference.AddressMaxLength).IsRequired();
        builder.Property(p => p.Purpose).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Source).HasMaxLength(CommunicationPreference.SourceMaxLength).IsRequired();
        builder.Property(p => p.EvidenceRef).HasMaxLength(CommunicationPreference.EvidenceMaxLength);
        builder.HasIndex(p => new { p.TenantId, p.Channel, p.Address, p.Purpose }).IsUnique();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
    }
}

internal sealed class ConsentEventConfiguration : IEntityTypeConfiguration<ConsentEvent>
{
    public void Configure(EntityTypeBuilder<ConsentEvent> builder)
    {
        builder.ToTable("ConsentEvents", DatabaseSchemas.Core);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(CommunicationPreference.SourceMaxLength).IsRequired();
        builder.Property(e => e.EvidenceRef).HasMaxLength(CommunicationPreference.EvidenceMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(CommunicationPreference.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.PreferenceId, e.ChangedAt });
        builder.HasOne<CommunicationPreference>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.PreferenceId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CommunicationSuppressionConfiguration : IEntityTypeConfiguration<CommunicationSuppression>
{
    public void Configure(EntityTypeBuilder<CommunicationSuppression> builder)
    {
        builder.ToTable("CommunicationSuppressions", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Address).HasMaxLength(CommunicationPreference.AddressMaxLength).IsRequired();
        builder.Property(s => s.Reason).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Source).HasMaxLength(CommunicationPreference.SourceMaxLength).IsRequired();
        builder.Property(s => s.Note).HasMaxLength(CommunicationSuppression.NoteMaxLength);
        builder.Property(s => s.LiftReason).HasMaxLength(CommunicationPreference.ReasonMaxLength);

        // Karar sorgusu (kanal + adres) ve liste filtreleri bu indeksi kullanır.
        builder.HasIndex(s => new { s.TenantId, s.Channel, s.Address });
        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
    }
}

internal sealed class FrequencyWindowConfiguration : IEntityTypeConfiguration<FrequencyWindow>
{
    public void Configure(EntityTypeBuilder<FrequencyWindow> builder)
    {
        builder.ToTable("FrequencyWindows", DatabaseSchemas.Core);
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.TenantId).IsRequired();
        builder.Property(w => w.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(w => w.Address).HasMaxLength(FrequencyWindow.AddressMaxLength).IsRequired();
        builder.Property(w => w.Day).IsRequired();
        builder.Property(w => w.Count).IsRequired();

        // Atomik rezervasyonun dayanağı: tenant, kanal, adres ve gün başına tek satır.
        builder.HasIndex(w => new { w.TenantId, w.Channel, w.Address, w.Day }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_FrequencyWindows_Count", "[Count] >= 0"));
    }
}
