using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Customization;

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
