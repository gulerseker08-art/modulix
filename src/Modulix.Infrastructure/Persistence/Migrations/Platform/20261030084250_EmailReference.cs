using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class EmailReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reference",
                schema: "platform",
                table: "EmailMessages",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_TenantId_Reference",
                schema: "platform",
                table: "EmailMessages",
                columns: new[] { "TenantId", "Reference" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailMessages_TenantId_Reference",
                schema: "platform",
                table: "EmailMessages");

            migrationBuilder.DropColumn(
                name: "Reference",
                schema: "platform",
                table: "EmailMessages");
        }
    }
}
