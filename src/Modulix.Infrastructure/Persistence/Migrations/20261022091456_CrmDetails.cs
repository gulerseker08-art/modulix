using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LostReason",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                schema: "crm",
                table: "Opportunities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Probability",
                schema: "crm",
                table: "Opportunities",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Industry",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                schema: "crm",
                table: "Companies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "crm",
                table: "Companies",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "crm",
                table: "Companies",
                type: "int",
                nullable: false,
                // Mevcut firmalar "Potansiyel" (CompanyStatus.Prospect) olarak başlar.
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                schema: "crm",
                table: "Activities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_OwnerId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_OwnerId",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_OwnerId_CompletedAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OwnerId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_OwnerId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_OwnerId",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_OwnerId_CompletedAt",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "LostReason",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Probability",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Address",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Industry",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Tags",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                schema: "crm",
                table: "Activities");
        }
    }
}
