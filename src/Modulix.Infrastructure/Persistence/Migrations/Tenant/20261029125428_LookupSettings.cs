using System;
using Microsoft.EntityFrameworkCore.Migrations;


#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class LookupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LookupSettings",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LookupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Searchable = table.Column<bool>(type: "bit", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupSettings", x => x.Id);
                    table.UniqueConstraint("AK_LookupSettings_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_LookupSettings_TenantId_LookupKey",
                schema: "meta",
                table: "LookupSettings",
                columns: new[] { "TenantId", "LookupKey" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "meta", "LookupSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "LookupSettings");

            migrationBuilder.DropTable(
                name: "LookupSettings",
                schema: "meta");
        }
    }
}
