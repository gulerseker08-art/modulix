using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        CrmTables.ConfigureCommon(builder, "TaxRates");
        builder.Property(t => t.Code).HasMaxLength(TaxRate.CodeMaxLength).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(TaxRate.NameMaxLength).IsRequired();
        builder.Property(t => t.RatePercent).HasPrecision(9, 4);
        builder.Property(t => t.ValidFrom).HasColumnType("date");
        builder.Property(t => t.ValidTo).HasColumnType("date");
        builder.HasIndex(t => new { t.TenantId, t.Code, t.ValidFrom }).IsUnique().HasDatabaseName("UX_TaxRates_CodeFrom");
        builder.ToTable(t => t.HasCheckConstraint("CK_TaxRates_Rate", "[RatePercent] >= 0 AND [RatePercent] <= 100"));
        builder.ToTable(t => t.HasCheckConstraint("CK_TaxRates_Dates", "[ValidTo] IS NULL OR [ValidFrom] < [ValidTo]"));
    }
}

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        CrmTables.ConfigureCommon(builder, "ExchangeRates");
        builder.Property(r => r.FromCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.ToCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.Rate).HasPrecision(28, 10);
        builder.Property(r => r.ValidOn).HasColumnType("date");
        builder.Property(r => r.Source).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(500);
        builder.HasIndex(r => new { r.TenantId, r.FromCurrency, r.ToCurrency, r.ValidOn }).IsUnique().HasDatabaseName("UX_ExchangeRates_PairDay");
        builder.ToTable(t => t.HasCheckConstraint("CK_ExchangeRates_Rate", "[Rate] > 0"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ExchangeRates_Pair", "[FromCurrency] <> [ToCurrency]"));
    }
}

internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> builder)
    {
        CrmTables.ConfigureCommon(builder, "PriceLists");
        builder.Property(l => l.Name).HasMaxLength(PriceList.NameMaxLength).IsRequired();
        builder.Property(l => l.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(l => l.Scope).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Segment).HasMaxLength(100);
        builder.Property(l => l.Status).HasMaxLength(20).IsRequired();
        builder.Property(l => l.TierMethod).HasMaxLength(20).IsRequired();
        builder.HasIndex(l => new { l.TenantId, l.Status, l.Scope });
        builder.HasIndex(l => new { l.TenantId, l.CompanyId });
        builder.ToTable(t => t.HasCheckConstraint("CK_PriceLists_Dates", "[ValidFrom] IS NULL OR [ValidTo] IS NULL OR [ValidFrom] < [ValidTo]"));
        builder.HasOne<Company>().WithMany().HasForeignKey(l => new { l.TenantId, l.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PriceListItemConfiguration : IEntityTypeConfiguration<PriceListItem>
{
    public void Configure(EntityTypeBuilder<PriceListItem> builder)
    {
        CrmTables.ConfigureCommon(builder, "PriceListItems");
        builder.Property(i => i.MinQuantity).HasPrecision(28, 10);
        builder.Property(i => i.MaxQuantity).HasPrecision(28, 10);
        builder.Property(i => i.UnitPrice).HasPrecision(28, 10);
        builder.HasIndex(i => new { i.TenantId, i.PriceListId, i.ProductId, i.MinQuantity }).IsUnique().HasDatabaseName("UX_PriceListItems_Tier");
        builder.HasIndex(i => new { i.TenantId, i.ProductId });
        builder.ToTable(t => t.HasCheckConstraint("CK_PriceListItems_Tier",
            "[MinQuantity] >= 0 AND ([MaxQuantity] IS NULL OR [MaxQuantity] > [MinQuantity]) AND [UnitPrice] >= 0 AND ([IsFree] = 0 OR [UnitPrice] = 0)"));
        builder.HasOne(i => i.PriceList).WithMany().HasForeignKey(i => new { i.TenantId, i.PriceListId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.Product).WithMany().HasForeignKey(i => new { i.TenantId, i.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductComponentConfiguration : IEntityTypeConfiguration<ProductComponent>
{
    public void Configure(EntityTypeBuilder<ProductComponent> builder)
    {
        CrmTables.ConfigureCommon(builder, "ProductComponents");
        builder.Property(c => c.Quantity).HasPrecision(28, 10);
        builder.HasIndex(c => new { c.TenantId, c.PackageId, c.ComponentId }).IsUnique().HasDatabaseName("UX_ProductComponents_Pair");
        builder.ToTable(t => t.HasCheckConstraint("CK_ProductComponents_Quantity", "[Quantity] > 0 AND [PackageId] <> [ComponentId]"));
        builder.HasOne(c => c.Package).WithMany().HasForeignKey(c => new { c.TenantId, c.PackageId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.Component).WithMany().HasForeignKey(c => new { c.TenantId, c.ComponentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DiscountPolicyConfiguration : IEntityTypeConfiguration<DiscountPolicy>
{
    public void Configure(EntityTypeBuilder<DiscountPolicy> builder)
    {
        CrmTables.ConfigureCommon(builder, "DiscountPolicies");
        builder.Property(d => d.Name).HasMaxLength(DiscountPolicy.NameMaxLength).IsRequired();
        builder.Property(d => d.Percent).HasPrecision(9, 4);
        builder.Property(d => d.Amount).HasPrecision(18, 2);
        builder.Property(d => d.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(d => d.MaxTotalPercent).HasPrecision(9, 4);
        builder.Property(d => d.ApprovalAbovePercent).HasPrecision(9, 4);
        builder.Property(d => d.ValidFrom).HasColumnType("date");
        builder.Property(d => d.ValidTo).HasColumnType("date");
        builder.HasIndex(d => new { d.TenantId, d.IsActive, d.Priority });
        builder.ToTable(t => t.HasCheckConstraint("CK_DiscountPolicies_Value",
            "([Percent] IS NOT NULL AND [Amount] IS NULL AND [Percent] > 0 AND [Percent] <= 100) OR ([Percent] IS NULL AND [Amount] IS NOT NULL AND [Amount] > 0 AND [Currency] IS NOT NULL)"));
    }
}
