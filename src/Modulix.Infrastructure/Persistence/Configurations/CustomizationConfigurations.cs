using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Customization;
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
