using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRequestSourceKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                schema: "core",
                table: "ApprovalRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_SourceKey",
                schema: "core",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "SourceKey" },
                unique: true,
                filter: "[SourceKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_TenantId_SourceKey",
                schema: "core",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                schema: "core",
                table: "ApprovalRequests");
        }
    }
}
