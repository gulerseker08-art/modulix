using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformGtipCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GtipCatalogVersions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    EntryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GtipCatalogVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GtipCatalogEntries",
                schema: "platform",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GtipCatalogEntries", x => new { x.VersionId, x.Code });
                    table.ForeignKey(
                        name: "FK_GtipCatalogEntries_GtipCatalogVersions_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "platform",
                        principalTable: "GtipCatalogVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GtipCatalogVersions_EffectiveFrom",
                schema: "platform",
                table: "GtipCatalogVersions",
                column: "EffectiveFrom",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GtipCatalogEntries",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "GtipCatalogVersions",
                schema: "platform");
        }
    }
}
