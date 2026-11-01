using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Persistence;

// Destek tabloları "support" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo Row-Level Security
// politikasına eklenir (TenantIsolationSql.AddTable). Kayıtlar arası bağlar (TenantId, Id) üzerinden kurulur: başka tenant'ın kaydına
// veritabanı düzeyinde de bağlanılamaz.

internal static class SupportTables
{
    public const string Schema = SupportModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : SupportEntity
    {
        builder.ToTable(table, Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.Property(e => e.CustomFields);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class SupportQueueConfiguration : IEntityTypeConfiguration<SupportQueue>
{
    public void Configure(EntityTypeBuilder<SupportQueue> builder)
    {
        SupportTables.ConfigureCommon(builder, "Queues");
        builder.Property(q => q.Name).HasMaxLength(SupportQueue.NameMaxLength).IsRequired();
        builder.Property(q => q.Description).HasMaxLength(SupportQueue.DescriptionMaxLength);

        builder.HasIndex(q => new { q.TenantId, q.Name }).IsUnique().HasDatabaseName("UX_Queues_Name");
        // Tenant başına en çok bir varsayılan kuyruk.
        builder.HasIndex(q => q.TenantId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("UX_Queues_Default");
    }
}

internal sealed class TicketCategoryConfiguration : IEntityTypeConfiguration<TicketCategory>
{
    public void Configure(EntityTypeBuilder<TicketCategory> builder)
    {
        SupportTables.ConfigureCommon(builder, "Categories");
        builder.Property(c => c.Name).HasMaxLength(TicketCategory.NameMaxLength).IsRequired();

        builder.Property(c => c.RequiredFields).HasMaxLength(200);

        builder.HasIndex(c => new { c.TenantId, c.ParentId, c.Name }).IsUnique().HasDatabaseName("UX_Categories_Name");

        builder.HasOne<TicketCategory>().WithMany().HasForeignKey(c => new { c.TenantId, c.ParentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SupportQueue>().WithMany().HasForeignKey(c => new { c.TenantId, c.DefaultQueueId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        SupportTables.ConfigureCommon(builder, "Tickets");
        builder.Property(t => t.Number).HasMaxLength(SupportTicket.NumberMaxLength).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(SupportTicket.SubjectMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(SupportTicket.TextMaxLength);
        builder.Property(t => t.TypeKey).HasMaxLength(SupportTicket.KeyMaxLength).IsRequired();
        builder.Property(t => t.Channel).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(t => t.CustomerName).HasMaxLength(SupportTicket.NameMaxLength);
        builder.Property(t => t.ReporterName).HasMaxLength(SupportTicket.NameMaxLength);
        builder.Property(t => t.ReporterEmail).HasMaxLength(SupportTicket.EmailMaxLength);
        builder.Property(t => t.ReporterPhone).HasMaxLength(SupportTicket.PhoneMaxLength);
        builder.Property(t => t.Impact).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(t => t.Urgency).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(t => t.Priority).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(t => t.PriorityReason).HasMaxLength(SupportTicket.ReasonMaxLength);
        builder.Property(t => t.Status).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(t => t.StatusReason).HasMaxLength(SupportTicket.ReasonMaxLength);
        builder.Property(t => t.ClientKey).HasMaxLength(SupportTicket.ClientKeyMaxLength);
        builder.Property(t => t.EventDate).HasColumnType("date");
        builder.Property(t => t.ExpectedOutcome).HasMaxLength(SupportTicket.TextMaxLength);
        builder.Property(t => t.ResolutionCode).HasMaxLength(SupportTicket.KeyMaxLength);
        builder.Property(t => t.ResolutionNote).HasMaxLength(SupportTicket.TextMaxLength);
        builder.Property(t => t.RootCause).HasMaxLength(SupportTicket.TextMaxLength);
        builder.Property(t => t.CorrectiveAction).HasMaxLength(SupportTicket.TextMaxLength);

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique().HasDatabaseName("UX_Tickets_Number");
        // Tekrar koruması: aynı istemci anahtarıyla ikinci talep açılamaz (anahtar verilmeyen taleplerde kısıt yok).
        builder.HasIndex(t => new { t.TenantId, t.ClientKey }).IsUnique().HasFilter("[ClientKey] IS NOT NULL").HasDatabaseName("UX_Tickets_ClientKey");
        builder.HasIndex(t => new { t.TenantId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.OwnerId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.QueueId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.PartyId });
        builder.HasIndex(t => new { t.TenantId, t.CategoryId });

        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Status",
            "[Status] IN ('New','Triage','Assigned','InProgress','WaitingCustomer','WaitingInternal','Resolved','Closed','Reopened','Cancelled','Merged')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Channel", "[Channel] IN ('User','Phone','Email','Web','Portal','Api')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Impact", "[Impact] IN ('Low','Medium','High')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Urgency", "[Urgency] IN ('Low','Medium','High')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Priority", "[Priority] IN ('Low','Normal','High','Critical')"));
        // Müşteri tanımlıysa ya da başvuran adı verilmişse kayıt anlamlıdır; ikisi de boş talep olmaz.
        builder.ToTable(t => t.HasCheckConstraint("CK_Tickets_Applicant", "[PartyId] IS NOT NULL OR [PersonId] IS NOT NULL OR [ReporterName] IS NOT NULL"));

        // Ortak kayıtlar (ADR-0006): müşteri taraf ve kişi aynı tenant'ta olmalıdır.
        builder.HasOne<Party>().WithMany().HasForeignKey(t => new { t.TenantId, t.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Person>().WithMany().HasForeignKey(t => new { t.TenantId, t.PersonId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TicketCategory>().WithMany().HasForeignKey(t => new { t.TenantId, t.CategoryId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SupportQueue>().WithMany().HasForeignKey(t => new { t.TenantId, t.QueueId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(t => new { t.TenantId, t.ParentTicketId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(t => new { t.TenantId, t.MergedIntoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.TenantId, t.MergedIntoId });
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        SupportTables.ConfigureCommon(builder, "Messages");
        builder.Property(m => m.Visibility).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(m => m.Direction).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(TicketMessage.BodyMaxLength).IsRequired();
        builder.Property(m => m.DeliveryStatus).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();

        builder.Property(m => m.Channel).HasMaxLength(SupportTicket.StatusMaxLength);
        builder.Property(m => m.ExternalMessageId).HasMaxLength(Lifecycle.InboundRules.MaxMessageIdLength);
        builder.Property(m => m.InReplyTo).HasMaxLength(Lifecycle.InboundRules.MaxMessageIdLength);
        builder.Property(m => m.SenderAddress).HasMaxLength(SupportTicket.EmailMaxLength);

        builder.HasIndex(m => new { m.TenantId, m.TicketId, m.CreatedAt });
        // Tekrar koruması: aynı kanalın aynı mesaj kimliği bir tenant'ta yalnızca bir kez yazılır (yarışta da).
        builder.HasIndex(m => new { m.TenantId, m.Channel, m.ExternalMessageId }).IsUnique()
            .HasFilter("[ExternalMessageId] IS NOT NULL").HasDatabaseName("UX_Messages_External");
        builder.HasIndex(m => new { m.TenantId, m.SenderAddress, m.CreatedAt }).HasFilter("[SenderAddress] IS NOT NULL");
        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(m => new { m.TenantId, m.SourceTicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);

        // İç not müşteriye görünen kanala giremez: iç görünürlük yalnızca not yönüyle, dış görünürlük yalnızca giden/gelen yönle birlikte olur.
        builder.ToTable(t => t.HasCheckConstraint("CK_Messages_Visibility",
            "([Visibility] = 'Internal' AND [Direction] = 'Note') OR ([Visibility] = 'Public' AND [Direction] IN ('Outbound','Inbound'))"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Messages_Delivery", "[DeliveryStatus] IN ('NotRequired','Recorded','Queued','Sent','Failed')"));

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(m => new { m.TenantId, m.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        SupportTables.ConfigureCommon(builder, "TicketEvents");
        builder.Property(e => e.Kind).HasMaxLength(SupportTicket.StatusMaxLength * 2).IsRequired();
        builder.Property(e => e.FromStatus).HasMaxLength(SupportTicket.StatusMaxLength);
        builder.Property(e => e.ToStatus).HasMaxLength(SupportTicket.StatusMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(SupportTicket.ReasonMaxLength);

        builder.HasIndex(e => new { e.TenantId, e.TicketId, e.CreatedAt });

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(e => new { e.TenantId, e.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
