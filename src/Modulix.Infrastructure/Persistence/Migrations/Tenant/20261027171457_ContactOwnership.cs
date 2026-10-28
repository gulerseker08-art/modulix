using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Modulix.Infrastructure.Persistence;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class ContactOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                schema: "crm",
                table: "Contacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_OwnerId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "OwnerId" });

            // Backfill (ADR-0010): mevcut kişinin sorumlusu, kaydı oluşturan kullanıcıdır (iş audit kaydından). Audit kaydı
            // bulunamayan kişi sorumlusuz kalır; yalnızca "tüm kayıtlar" kapsamı firmasız ise onu görür. Hiçbir satır silinmez.
            // RLS politikası oturumda tenant değeri ister; migration tüm satırlara dokunduğu için politika yalnızca bu deyim için kapatılır.
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = OFF);");
            migrationBuilder.Sql(@"
                UPDATE c SET c.OwnerId = a.UserId
                FROM [crm].[Contacts] c
                CROSS APPLY (SELECT TOP (1) l.UserId FROM [core].[AuditLog] l
                             WHERE l.TenantId = c.TenantId AND l.RecordId = c.Id AND l.EntityKey = N'crm.contact' AND l.Action = 1
                             ORDER BY l.OccurredAt) a
                WHERE c.OwnerId IS NULL;");
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_OwnerId",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                schema: "crm",
                table: "Contacts");
        }
    }
}
