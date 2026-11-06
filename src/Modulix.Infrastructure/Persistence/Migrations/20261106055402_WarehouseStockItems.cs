using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehouseStockItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockItems",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Gtip = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItems", x => x.Id);
                    table.UniqueConstraint("AK_StockItems_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_TenantId_Gtip",
                schema: "warehouse",
                table: "StockItems",
                columns: new[] { "TenantId", "Gtip" });

            migrationBuilder.CreateIndex(
                name: "UX_StockItems_Code",
                schema: "warehouse",
                table: "StockItems",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema ve GRANT ilk depo migration'ındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "StockItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "StockItems");

            migrationBuilder.DropTable(
                name: "StockItems",
                schema: "warehouse");
        }
    }
}
