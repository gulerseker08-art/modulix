using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRecordTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetEntityKey",
                schema: "core",
                table: "ApprovalRequests",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetLabel",
                schema: "core",
                table: "ApprovalRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetRecordId",
                schema: "core",
                table: "ApprovalRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetEntityKey",
                schema: "core",
                table: "ApprovalDefinitions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_TargetEntityKey_TargetRecordId_Status",
                schema: "core",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "TargetEntityKey", "TargetRecordId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_TenantId_TargetEntityKey_TargetRecordId_Status",
                schema: "core",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "TargetEntityKey",
                schema: "core",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "TargetLabel",
                schema: "core",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "TargetRecordId",
                schema: "core",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "TargetEntityKey",
                schema: "core",
                table: "ApprovalDefinitions");
        }
    }
}
