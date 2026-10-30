using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class TenantLedgerRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_DatabaseName",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DatabaseName",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ProvisioningStatus",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                schema: "platform",
                table: "Tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DatabaseName",
                schema: "platform",
                table: "Tenants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisioningStatus",
                schema: "platform",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SchemaVersion",
                schema: "platform",
                table: "Tenants",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_DatabaseName",
                schema: "platform",
                table: "Tenants",
                column: "DatabaseName",
                unique: true,
                filter: "[DatabaseName] IS NOT NULL");
        }
    }
}
