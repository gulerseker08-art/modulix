using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessUnitRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignLevel",
                schema: "core",
                table: "Roles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "BusinessUnitRoles",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InheritToChildren = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessUnitRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessUnitRoles_BusinessUnits_TenantId_BusinessUnitId",
                        columns: x => new { x.TenantId, x.BusinessUnitId },
                        principalSchema: "core",
                        principalTable: "BusinessUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessUnitRoles_Roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "core",
                        principalTable: "Roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnitRoles_TenantId_BusinessUnitId_RoleId",
                schema: "core",
                table: "BusinessUnitRoles",
                columns: new[] { "TenantId", "BusinessUnitId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnitRoles_TenantId_RoleId",
                schema: "core",
                table: "BusinessUnitRoles",
                columns: new[] { "TenantId", "RoleId" });

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Mevcut roller
            // "yalnızca kullanıcıya atanır" (AssignLevel=1) olarak başlar; departmana atama açık işaretlemeyle başlar (ADR-0026 K6).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "BusinessUnitRoles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "BusinessUnitRoles");

            migrationBuilder.DropTable(
                name: "BusinessUnitRoles",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "AssignLevel",
                schema: "core",
                table: "Roles");
        }
    }
}
