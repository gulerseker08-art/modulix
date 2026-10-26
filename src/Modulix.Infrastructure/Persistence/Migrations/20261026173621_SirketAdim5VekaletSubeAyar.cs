using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SirketAdim5VekaletSubeAyar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "core",
                table: "TenantSettings");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                schema: "core",
                table: "TenantSettings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                schema: "core",
                table: "MembershipCompanies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValidFrom",
                schema: "core",
                table: "MembershipCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValidUntil",
                schema: "core",
                table: "MembershipCompanies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_CompanyId_Key",
                schema: "core",
                table: "TenantSettings",
                columns: new[] { "TenantId", "CompanyId", "Key" },
                unique: true,
                filter: "[CompanyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "core",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true,
                filter: "[CompanyId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_BranchId",
                schema: "core",
                table: "MembershipCompanies",
                column: "BranchId");

            migrationBuilder.AddForeignKey(
                name: "FK_MembershipCompanies_Branches_BranchId",
                schema: "core",
                table: "MembershipCompanies",
                column: "BranchId",
                principalSchema: "core",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MembershipCompanies_Branches_BranchId",
                schema: "core",
                table: "MembershipCompanies");

            migrationBuilder.DropIndex(
                name: "IX_TenantSettings_TenantId_CompanyId_Key",
                schema: "core",
                table: "TenantSettings");

            migrationBuilder.DropIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "core",
                table: "TenantSettings");

            migrationBuilder.DropIndex(
                name: "IX_MembershipCompanies_BranchId",
                schema: "core",
                table: "MembershipCompanies");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                schema: "core",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BranchId",
                schema: "core",
                table: "MembershipCompanies");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                schema: "core",
                table: "MembershipCompanies");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                schema: "core",
                table: "MembershipCompanies");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "core",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);
        }
    }
}
