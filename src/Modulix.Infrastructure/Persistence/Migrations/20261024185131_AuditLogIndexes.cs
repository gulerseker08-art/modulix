using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_OccurredAt",
                schema: "core",
                table: "AuditLog",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_UserId_OccurredAt",
                schema: "core",
                table: "AuditLog",
                columns: new[] { "TenantId", "UserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLog_TenantId_OccurredAt",
                schema: "core",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_TenantId_UserId_OccurredAt",
                schema: "core",
                table: "AuditLog");
        }
    }
}
