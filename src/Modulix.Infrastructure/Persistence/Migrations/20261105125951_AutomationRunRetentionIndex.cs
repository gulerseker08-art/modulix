using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationRunRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_CompletedAt",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationRuns_TenantId_CompletedAt",
                schema: "core",
                table: "AutomationRuns");
        }
    }
}
