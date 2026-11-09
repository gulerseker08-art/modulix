using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Approvals;
using Modulix.Core.Notifications;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalDefinitionConfiguration : IEntityTypeConfiguration<ApprovalDefinition>
{
    public void Configure(EntityTypeBuilder<ApprovalDefinition> builder)
    {
        // Tenant verisi (ADR-0027 Dilim 4a): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("ApprovalDefinitions", DatabaseSchemas.Core);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.Name).HasMaxLength(ApprovalDefinition.NameMaxLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(ApprovalDefinition.DescriptionMaxLength);
        builder.Property(d => d.Currency).HasMaxLength(3);
        builder.Property(d => d.StepsJson).IsRequired();
        builder.Property(d => d.TargetEntityKey).HasMaxLength(100);
        builder.Property(d => d.GatedCommands).HasMaxLength(ApprovalDefinition.GatedCommandsMaxLength);
        builder.Property(d => d.Version).IsConcurrencyToken();
        builder.Ignore(d => d.Steps);

        builder.HasAlternateKey(d => new { d.TenantId, d.Id });
        builder.HasIndex(d => new { d.TenantId, d.Name }).IsUnique();
    }
}

internal sealed class ApprovalDelegationConfiguration : IEntityTypeConfiguration<ApprovalDelegation>
{
    public void Configure(EntityTypeBuilder<ApprovalDelegation> builder)
    {
        builder.ToTable("ApprovalDelegations", DatabaseSchemas.Core);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.Reason).HasMaxLength(ApprovalDelegation.ReasonMaxLength);

        builder.HasIndex(d => new { d.TenantId, d.DelegateUserId, d.RevokedAt });
        builder.HasIndex(d => new { d.TenantId, d.PrincipalUserId, d.RevokedAt });
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", DatabaseSchemas.Core);
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.TenantId).IsRequired();
        builder.Property(n => n.Kind).HasMaxLength(Notification.KindMaxLength).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(Notification.TitleMaxLength).IsRequired();
        builder.Property(n => n.Link).HasMaxLength(Notification.LinkMaxLength);
        builder.Property(n => n.DedupeKey).HasMaxLength(Notification.DedupeKeyMaxLength).IsRequired();

        // Aynı olay aynı kişiye iki kez düşmez (Ç5a).
        builder.HasIndex(n => new { n.TenantId, n.UserId, n.DedupeKey }).IsUnique();
        builder.HasIndex(n => new { n.TenantId, n.UserId, n.ReadAt, n.CreatedAt });
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.DefinitionName).HasMaxLength(ApprovalDefinition.NameMaxLength).IsRequired();
        builder.Property(r => r.Title).HasMaxLength(ApprovalRequest.TitleMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(ApprovalRequest.DescriptionMaxLength);
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Property(r => r.Currency).HasMaxLength(3);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Version).IsConcurrencyToken();
        builder.Property(r => r.TargetEntityKey).HasMaxLength(100);
        builder.Property(r => r.TargetLabel).HasMaxLength(ApprovalRequest.TitleMaxLength);
        builder.Property(r => r.SourceKey).HasMaxLength(ApprovalRequest.SourceKeyMaxLength);

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasOne<ApprovalDefinition>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.DefinitionId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.Status, r.SubmittedAt });
        builder.HasIndex(r => new { r.TenantId, r.RequesterId, r.Status });

        // Kayıt ekranı ve geçersizleştirme kaydın taleplerini arar.
        builder.HasIndex(r => new { r.TenantId, r.TargetEntityKey, r.TargetRecordId, r.Status });

        // Kural kaynaklı talep (Dilim 4c-2): açan kural adımının tekilleştirme anahtarı; aynı anahtarla ikinci talep açılmaz.
        builder.HasIndex(r => new { r.TenantId, r.SourceKey }).IsUnique().HasFilter("[SourceKey] IS NOT NULL");
    }
}

internal sealed class ApprovalRequestStepConfiguration : IEntityTypeConfiguration<ApprovalRequestStep>
{
    public void Configure(EntityTypeBuilder<ApprovalRequestStep> builder)
    {
        builder.ToTable("ApprovalRequestSteps", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(ApprovalDefinition.StepNameMaxLength).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Comment).HasMaxLength(ApprovalRequest.CommentMaxLength);

        builder.HasOne<ApprovalRequest>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.RequestId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Bekleyen işler kutusu etkin adımı kişiye ya da role göre arar.
        builder.HasIndex(s => new { s.TenantId, s.RequestId, s.StepOrder });
        builder.HasIndex(s => new { s.TenantId, s.Status, s.ApproverUserId });
        builder.HasIndex(s => new { s.TenantId, s.Status, s.ApproverRoleId });
    }
}
