using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                schema: "core",
                table: "Roles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_SystemRole",
                schema: "core",
                table: "Roles",
                column: "TenantId",
                unique: true,
                filter: "[IsSystem] = 1");

            // Eski "Yönetici" rolleri sistem rolüne çevrilir, yöneticiler yeni yönetim izinlerini alır.
            migrationBuilder.Sql(SystemRoleUpgradeSql.AllTenants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_TenantId_SystemRole",
                schema: "core",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                schema: "core",
                table: "Roles");
        }
    }
}
