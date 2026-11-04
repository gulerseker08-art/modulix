using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Automation;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        // Tenant verisi (ADR-0027): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("AutomationRules", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(AutomationRule.NameMaxLength).IsRequired();
        builder.Property(r => r.ModuleKey).HasMaxLength(30).IsRequired();
        builder.Property(r => r.EntityKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();
        builder.Property(r => r.Version).IsConcurrencyToken();

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        // Listeler modül ve duruma göre süzülür.
        builder.HasIndex(r => new { r.TenantId, r.ModuleKey, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.EntityKey, r.Status });
    }
}

internal sealed class AutomationRuleVersionConfiguration : IEntityTypeConfiguration<AutomationRuleVersion>
{
    public void Configure(EntityTypeBuilder<AutomationRuleVersion> builder)
    {
        builder.ToTable("AutomationRuleVersions", DatabaseSchemas.Core);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.TenantId).IsRequired();
        builder.Property(v => v.VersionNo).IsRequired();
        builder.Property(v => v.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(AutomationRule.NameMaxLength).IsRequired();
        builder.Property(v => v.Description).HasMaxLength(AutomationRuleVersion.DescriptionMaxLength);
        builder.Property(v => v.DefinitionJson).IsRequired();
        builder.Property(v => v.DefinitionHash).HasMaxLength(64).IsRequired();

        builder.HasAlternateKey(v => new { v.TenantId, v.Id });

        // Sürüm numarası kural içinde tekildir (benzersizlik tenant ile birlikte).
        builder.HasIndex(v => new { v.TenantId, v.RuleId, v.VersionNo }).IsUnique();

        // Kural başına en çok bir taslak.
        builder.HasIndex(v => new { v.TenantId, v.RuleId })
            .IsUnique()
            .HasFilter("[State] = N'Draft'")
            .HasDatabaseName("IX_AutomationRuleVersions_TenantId_RuleId_Draft");

        // Bileşik yabancı anahtar (ADR-0020): sürüm ile kural aynı tenant'ta olmak zorundadır. Yayınlanmış sürümü olan kural
        // silinmediği için kısıtlıdır; hiç yayınlanmamış kuralın taslağı kuralla birlikte silinir (uygulama önce sürümü siler).
        builder.HasOne<AutomationRule>()
            .WithMany()
            .HasForeignKey(v => new { v.TenantId, v.RuleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    public void Configure(EntityTypeBuilder<AutomationRun> builder)
    {
        builder.ToTable("AutomationRuns", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.EntityKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Origin).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.DedupeKey).HasMaxLength(AutomationRun.DedupeKeyLength).IsRequired();
        builder.Property(r => r.CorrelationId).HasMaxLength(AutomationRun.CorrelationMaxLength);
        builder.Property(r => r.Note).HasMaxLength(AutomationRun.NoteMaxLength);

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        // Tekilleştirme (ADR-0027): aynı olay + kural sürümü + hedef kayıt için tenant içinde tek çalışma; benzersizlik tenant ile birlikte.
        builder.HasIndex(r => new { r.TenantId, r.DedupeKey }).IsUnique();

        // İşleyici sıradaki çalışmaları (durum + sıra zamanı) bu indeksle seçer.
        builder.HasIndex(r => new { r.TenantId, r.Status, r.NextAttemptAt });

        // Çalışma geçmişi kurala ve kayda göre listelenir; döngü korumasının "aynı iz" sorgusu da buradan geçer.
        builder.HasIndex(r => new { r.TenantId, r.RuleId, r.CreatedAt });
        builder.HasIndex(r => new { r.TenantId, r.RuleId, r.RecordId });
        builder.HasIndex(r => new { r.TenantId, r.EntityKey, r.RecordId });

        // Çalışma, bağlı olduğu sürümle aynı tenant'tadır; sürüm silinmez (yayınlanmış sürüm değişmez ve geçmiş ona bağlıdır).
        builder.HasOne<AutomationRuleVersion>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RuleVersionId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutomationRunStepConfiguration : IEntityTypeConfiguration<AutomationRunStep>
{
    public void Configure(EntityTypeBuilder<AutomationRunStep> builder)
    {
        builder.ToTable("AutomationRunSteps", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.StepId).HasMaxLength(AutomationRunStep.StepIdMaxLength).IsRequired();
        builder.Property(s => s.Action).HasMaxLength(AutomationRunStep.ActionMaxLength).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.IdempotencyKey).HasMaxLength(AutomationRun.DedupeKeyLength).IsRequired();
        builder.Property(s => s.Summary).HasMaxLength(AutomationRunStep.SummaryMaxLength);
        builder.Property(s => s.EffectEntityKey).HasMaxLength(100);

        builder.HasIndex(s => new { s.TenantId, s.RunId, s.StepNo }).IsUnique();

        builder.HasOne<AutomationRun>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
