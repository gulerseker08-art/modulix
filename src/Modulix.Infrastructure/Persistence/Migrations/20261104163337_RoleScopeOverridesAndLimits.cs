using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleScopeOverridesAndLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleScopeOverrides",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleScopeOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleScopeOverrides_Roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "core",
                        principalTable: "Roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantScopeLimits",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MaxScope = table.Column<int>(type: "int", nullable: false),
                    AppliesToAdministrators = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantScopeLimits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantScopeLimits_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleScopeOverrides_TenantId_RoleId_EntityKey",
                schema: "core",
                table: "RoleScopeOverrides",
                columns: new[] { "TenantId", "RoleId", "EntityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantScopeLimits_TenantId_EntityKey",
                schema: "platform",
                table: "TenantScopeLimits",
                columns: new[] { "TenantId", "EntityKey" },
                unique: true);

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). TenantScopeLimits platform
            // tablosudur (RLS dışı); boş iki tablo eklenir, bugünkü davranış değişmez (ADR-0030 K7).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "RoleScopeOverrides");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "RoleScopeOverrides");

            migrationBuilder.DropTable(
                name: "RoleScopeOverrides",
                schema: "core");

            migrationBuilder.DropTable(
                name: "TenantScopeLimits",
                schema: "platform");
        }
    }
}
