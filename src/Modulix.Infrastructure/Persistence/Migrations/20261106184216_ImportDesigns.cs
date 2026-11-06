using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportDesigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportDesigns",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DesignJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportDesigns", x => x.Id);
                    table.UniqueConstraint("AK_ImportDesigns_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportDesigns_TenantId_EntityKey",
                schema: "meta",
                table: "ImportDesigns",
                columns: new[] { "TenantId", "EntityKey" },
                unique: true);

            // Tenant tablosu: Row-Level Security politikasına eklenir (ADR-0002, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "meta", "ImportDesigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "ImportDesigns");

            migrationBuilder.DropTable(
                name: "ImportDesigns",
                schema: "meta");
        }
    }
}
