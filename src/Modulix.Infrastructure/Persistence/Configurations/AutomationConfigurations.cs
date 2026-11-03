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
