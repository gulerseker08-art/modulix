using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommunicationCleanupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_FrequencyWindows_TenantId_Day",
                schema: "core",
                table: "FrequencyWindows",
                columns: new[] { "TenantId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationLinkTokens_TenantId_ExpiresAt",
                schema: "core",
                table: "CommunicationLinkTokens",
                columns: new[] { "TenantId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FrequencyWindows_TenantId_Day",
                schema: "core",
                table: "FrequencyWindows");

            migrationBuilder.DropIndex(
                name: "IX_CommunicationLinkTokens_TenantId_ExpiresAt",
                schema: "core",
                table: "CommunicationLinkTokens");
        }
    }
}
