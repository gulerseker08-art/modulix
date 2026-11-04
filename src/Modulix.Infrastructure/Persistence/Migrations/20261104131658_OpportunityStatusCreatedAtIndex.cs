using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpportunityStatusCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_Status_CreatedAt",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "Status", "CreatedAt", "Id" },
                descending: new[] { false, false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_Status_CreatedAt",
                schema: "crm",
                table: "Opportunities");
        }
    }
}
