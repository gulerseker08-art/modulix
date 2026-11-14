using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Customization;
using Modulix.Core.FieldRules;
using Modulix.Core.Lookups;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class CustomFieldConfiguration : IEntityTypeConfiguration<CustomField>
{
    public void Configure(EntityTypeBuilder<CustomField> builder)
    {
        // Tenant verisi (Y9a): meta şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("CustomFields", DatabaseSchemas.Meta);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.EntityKey).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Key).HasMaxLength(CustomField.KeyMaxLength).IsRequired();
        builder.Property(f => f.Label).HasMaxLength(CustomField.LabelMaxLength).IsRequired();
        builder.Property(f => f.Type).IsRequired();
        builder.Property(f => f.OptionsJson).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Ignore(f => f.Options);
        builder.Ignore(f => f.IsArchived);
        builder.Ignore(f => f.HasOptions);
        builder.HasIndex(f => new { f.TenantId, f.EntityKey, f.Key }).IsUnique();
        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
    }
}

internal sealed class TenantChoiceListConfiguration : IEntityTypeConfiguration<TenantChoiceList>
{
    public void Configure(EntityTypeBuilder<TenantChoiceList> builder)
    {
        // Tenant verisi (Y9b): yalnızca firmanın düzenlediği listeler; Row-Level Security ile korunur.
        builder.ToTable("ChoiceLists", DatabaseSchemas.Meta);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.ListKey).HasMaxLength(TenantChoiceList.ListKeyMaxLength).IsRequired();
        builder.Property(l => l.ValuesJson).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();
        builder.Ignore(l => l.Values);
        builder.HasIndex(l => new { l.TenantId, l.ListKey }).IsUnique();
        builder.HasAlternateKey(l => new { l.TenantId, l.Id });
    }
}

internal sealed class SubProfileConfiguration : IEntityTypeConfiguration<SubProfile>
{
    public void Configure(EntityTypeBuilder<SubProfile> builder)
    {
        // Tenant verisi (ADR-0041): firmanın alt profilleri; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("SubProfiles", DatabaseSchemas.Meta);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.Code).HasMaxLength(SubProfile.CodeMaxLength).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(SubProfile.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(SubProfile.DescriptionMaxLength);
        builder.Property(p => p.ActivityCode).HasMaxLength(50);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Revision).IsRequired().IsConcurrencyToken();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.Code }).IsUnique();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
    }
}

internal sealed class SubProfileOptionConfiguration : IEntityTypeConfiguration<SubProfileOption>
{
    public void Configure(EntityTypeBuilder<SubProfileOption> builder)
    {
        builder.ToTable("SubProfileOptions", DatabaseSchemas.Meta);
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.SubProfileId).IsRequired();
        builder.Property(o => o.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.TargetKey).HasMaxLength(SubProfileOption.TargetMaxLength).IsRequired();
        builder.Property(o => o.Label).HasMaxLength(SubProfileOption.LabelMaxLength).IsRequired();
        builder.Property(o => o.FieldType);
        builder.Property(o => o.ChoicesJson).IsRequired();
        builder.Property(o => o.Category).HasMaxLength(100);
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Ignore(o => o.Choices);
        builder.Ignore(o => o.IsApplied);
        builder.HasIndex(o => new { o.TenantId, o.SubProfileId, o.Kind, o.TargetKey, o.Label }).IsUnique();
        builder.HasAlternateKey(o => new { o.TenantId, o.Id });
        // Alt profil ile seçenek aynı tenant'tadır (bileşik yabancı anahtar); alt profil silinmediği için kaskad yoktur.
        builder.HasOne<SubProfile>().WithMany().HasForeignKey(o => new { o.TenantId, o.SubProfileId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubProfileFeatureConfiguration : IEntityTypeConfiguration<SubProfileFeature>
{
    public void Configure(EntityTypeBuilder<SubProfileFeature> builder)
    {
        // Tenant verisi (ADR-0041 dilim 4): alt profilin hizmet özellikleri; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("SubProfileFeatures", DatabaseSchemas.Meta);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.SubProfileId).IsRequired();
        builder.Property(f => f.Group).HasMaxLength(SubProfileFeature.GroupMaxLength).IsRequired();
        builder.Property(f => f.Value).HasMaxLength(SubProfileFeature.ValueMaxLength).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.HasIndex(f => new { f.TenantId, f.SubProfileId, f.Group, f.Value }).IsUnique();
        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
        builder.HasOne<SubProfile>().WithMany().HasForeignKey(f => new { f.TenantId, f.SubProfileId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubProfileRuleConfiguration : IEntityTypeConfiguration<SubProfileRule>
{
    public void Configure(EntityTypeBuilder<SubProfileRule> builder)
    {
        // Tenant verisi (ADR-0041 dilim 2): alt profilin alan kuralları (kural katmanına girer); Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("SubProfileRules", DatabaseSchemas.Meta);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.SubProfileId).IsRequired();
        builder.Property(r => r.Key).HasMaxLength(FieldRule.KeyMaxLength).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(SubProfileRule.NameMaxLength).IsRequired();
        builder.Property(r => r.RuleJson).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Key }).IsUnique();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasOne<SubProfile>().WithMany().HasForeignKey(r => new { r.TenantId, r.SubProfileId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantFormLayoutConfiguration : IEntityTypeConfiguration<TenantFormLayout>
{
    public void Configure(EntityTypeBuilder<TenantFormLayout> builder)
    {
        // Tenant verisi (ADR-0021): yalnızca firmanın özelleştirdiği formlar; Row-Level Security ile korunur.
        builder.ToTable("FormLayouts", DatabaseSchemas.Meta);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.EntityKey).HasMaxLength(TenantFormLayout.EntityKeyMaxLength).IsRequired();
        builder.Property(l => l.Version).IsRequired().IsConcurrencyToken();
        builder.Property(l => l.UpdatedAt).IsRequired();
        builder.HasIndex(l => new { l.TenantId, l.EntityKey }).IsUnique();
        builder.HasAlternateKey(l => new { l.TenantId, l.Id });
    }
}

internal sealed class TenantLookupSettingConfiguration : IEntityTypeConfiguration<TenantLookupSetting>
{
    public void Configure(EntityTypeBuilder<TenantLookupSetting> builder)
    {
        // Tenant verisi (ADR-0016): yalnızca firmanın özelleştirdiği lookup'lar; Row-Level Security ile korunur.
        builder.ToTable("LookupSettings", DatabaseSchemas.Meta);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.LookupKey).HasMaxLength(TenantLookupSetting.LookupKeyMaxLength).IsRequired();
        builder.Property(l => l.Version).IsRequired().IsConcurrencyToken();
        builder.Property(l => l.UpdatedAt).IsRequired();
        builder.HasIndex(l => new { l.TenantId, l.LookupKey }).IsUnique();
        builder.HasAlternateKey(l => new { l.TenantId, l.Id });
    }
}
