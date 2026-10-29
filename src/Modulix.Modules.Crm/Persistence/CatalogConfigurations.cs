using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        CrmTables.ConfigureCommon(builder, "ProductCategories");
        builder.Property(c => c.Name).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.ParentId });
        builder.HasOne<ProductCategory>().WithMany().HasForeignKey(c => new { c.TenantId, c.ParentId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MeasureUnitConfiguration : IEntityTypeConfiguration<MeasureUnit>
{
    public void Configure(EntityTypeBuilder<MeasureUnit> builder)
    {
        CrmTables.ConfigureCommon(builder, "MeasureUnits");
        builder.Property(u => u.Code).HasMaxLength(MeasureUnit.CodeMaxLength).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(MeasureUnit.NameMaxLength).IsRequired();
        builder.Property(u => u.GroupKey).HasMaxLength(20).IsRequired();
        builder.Property(u => u.FactorToBase).HasPrecision(28, 10);
        builder.HasIndex(u => new { u.TenantId, u.Code }).IsUnique().HasDatabaseName("UX_MeasureUnits_Code");
        builder.ToTable(t => t.HasCheckConstraint("CK_MeasureUnits_Factor", "[FactorToBase] > 0"));
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        CrmTables.ConfigureCommon(builder, "Products");
        builder.Property(p => p.Code).HasMaxLength(Product.CodeMaxLength).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Product.TextMaxLength);
        builder.Property(p => p.Kind).HasMaxLength(20).IsRequired();
        builder.Property(p => p.Status).HasMaxLength(20).IsRequired();
        builder.Property(p => p.ExternalCode).HasMaxLength(Product.ShortMaxLength);
        builder.Property(p => p.Barcode).HasMaxLength(Product.ShortMaxLength);
        builder.Property(p => p.Brand).HasMaxLength(Product.ShortMaxLength);
        builder.Property(p => p.Model).HasMaxLength(Product.ShortMaxLength);
        builder.Property(p => p.Tags).HasMaxLength(200);
        builder.Property(p => p.SaleStart).HasColumnType("date");
        builder.Property(p => p.SaleEnd).HasColumnType("date");
        builder.Property(p => p.TaxCategory).HasMaxLength(30);
        builder.Property(p => p.Cost).HasPrecision(18, 2);
        builder.Property(p => p.CostCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(p => p.DeliveryMode).HasMaxLength(20);
        builder.Property(p => p.ChargeType).HasMaxLength(20).IsRequired();
        builder.Property(p => p.RecurringPeriod).HasMaxLength(20);

        builder.HasIndex(p => new { p.TenantId, p.Code }).IsUnique().HasDatabaseName("UX_Products_Code");
        builder.HasIndex(p => new { p.TenantId, p.Status, p.Name });
        builder.HasIndex(p => new { p.TenantId, p.CategoryId });
        builder.ToTable(t => t.HasCheckConstraint("CK_Products_SaleDates", "[SaleStart] IS NULL OR [SaleEnd] IS NULL OR [SaleStart] <= [SaleEnd]"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Products_Precision", "[QuantityPrecision] BETWEEN 0 AND 4"));

        builder.HasOne(p => p.Category).WithMany().HasForeignKey(p => new { p.TenantId, p.CategoryId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.BaseUnit).WithMany().HasForeignKey(p => new { p.TenantId, p.BaseUnitId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
