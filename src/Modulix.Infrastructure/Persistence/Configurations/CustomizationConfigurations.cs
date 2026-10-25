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
