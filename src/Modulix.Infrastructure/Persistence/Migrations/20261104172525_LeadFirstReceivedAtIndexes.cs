using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeadFirstReceivedAtIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt",
                schema: "crm",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt_Id",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "FirstReceivedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_Status_FirstReceivedAt_Id",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "Status", "FirstReceivedAt", "Id" },
                descending: new[] { false, false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt_Id",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_Status_FirstReceivedAt_Id",
                schema: "crm",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "FirstReceivedAt" });
        }
    }
}
