using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class QuoteApprovalRuleConfiguration : IEntityTypeConfiguration<QuoteApprovalRule>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalRule> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteApprovalRules");
        builder.Property(r => r.Name).HasMaxLength(QuoteApprovalRule.NameMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(QuoteApprovalRule.DescriptionMaxLength);
        builder.Property(r => r.Currency).HasMaxLength(Quote.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.MinDiscountPercent).HasPrecision(9, 4);
        builder.Property(r => r.MinGrossTotal).HasPrecision(18, 2);
        builder.Property(r => r.Step).HasDefaultValue(1);
        builder.HasIndex(r => new { r.TenantId, r.IsActive, r.Priority });
        builder.ToTable(t => t.HasCheckConstraint("CK_QuoteApprovalRules_Condition",
            "([MinDiscountPercent] IS NOT NULL AND [MinDiscountPercent] >= 0 AND [MinDiscountPercent] < 100) OR ([MinGrossTotal] IS NOT NULL AND [MinGrossTotal] >= 0)"));
        builder.ToTable(t => t.HasCheckConstraint("CK_QuoteApprovalRules_Step", "[Step] BETWEEN 1 AND 9"));
    }
}
