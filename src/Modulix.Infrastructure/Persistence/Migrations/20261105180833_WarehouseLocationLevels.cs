using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehouseLocationLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocationLevels",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FacilityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CharacterSet = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    MinLength = table.Column<int>(type: "int", nullable: false),
                    MaxLength = table.Column<int>(type: "int", nullable: false),
                    Separator = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationLevels", x => x.Id);
                    table.UniqueConstraint("AK_LocationLevels_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_LocationLevels_Facilities_TenantId_FacilityId",
                        columns: x => new { x.TenantId, x.FacilityId },
                        principalSchema: "warehouse",
                        principalTable: "Facilities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_LocationLevels_Key",
                schema: "warehouse",
                table: "LocationLevels",
                columns: new[] { "TenantId", "FacilityId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_LocationLevels_Position",
                schema: "warehouse",
                table: "LocationLevels",
                columns: new[] { "TenantId", "FacilityId", "Position" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema ve GRANT önceki migration'dadır.
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "LocationLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "LocationLevels");

            migrationBuilder.DropTable(
                name: "LocationLevels",
                schema: "warehouse");
        }
    }
}
