using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrCoreDepartments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AudienceBusinessUnitId",
                schema: "hr",
                table: "Surveys",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessUnitId",
                schema: "hr",
                table: "Employees",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessUnitId",
                schema: "hr",
                table: "EmployeePositions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AudienceBusinessUnitId",
                schema: "hr",
                table: "Announcements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_BusinessUnit",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "BusinessUnitId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Employees_BusinessUnit",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "AudienceBusinessUnitId",
                schema: "hr",
                table: "Surveys");

            migrationBuilder.DropColumn(
                name: "BusinessUnitId",
                schema: "hr",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "BusinessUnitId",
                schema: "hr",
                table: "EmployeePositions");

            migrationBuilder.DropColumn(
                name: "AudienceBusinessUnitId",
                schema: "hr",
                table: "Announcements");
        }
    }
}
