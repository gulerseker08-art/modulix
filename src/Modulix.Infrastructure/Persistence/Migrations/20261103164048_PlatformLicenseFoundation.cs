using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformLicenseFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                schema: "platform",
                table: "ModuleEntitlements",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "platform",
                table: "ModuleEntitlements",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValidUntil",
                schema: "platform",
                table: "ModuleEntitlements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformAuditLog",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLog_OccurredAt",
                schema: "platform",
                table: "PlatformAuditLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLog_TargetTenantId_OccurredAt",
                schema: "platform",
                table: "PlatformAuditLog",
                columns: new[] { "TargetTenantId", "OccurredAt" });

            // Platform denetim kaydı append-only (ADR-0028): uygulama rolü UPDATE/DELETE yapamaz (rol yoksa, ör. tasarım zamanı, atlanır).
            // Mevcut ModuleEntitlements satırları Source=1 (Manual), süresiz olarak taşınır (sütun varsayılanı).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL DENY UPDATE, DELETE ON [platform].[PlatformAuditLog] TO [modulix_app_role];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAuditLog",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "Note",
                schema: "platform",
                table: "ModuleEntitlements");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "platform",
                table: "ModuleEntitlements");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                schema: "platform",
                table: "ModuleEntitlements");
        }
    }
}
