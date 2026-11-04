using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Reports",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DatasetKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DatasetVersion = table.Column<int>(type: "int", nullable: false),
                    SpecJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Visibility = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.UniqueConstraint("AK_Reports_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ReportFavorites",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportFavorites", x => x.Id);
                    table.UniqueConstraint("AK_ReportFavorites_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReportFavorites_Reports_TenantId_ReportId",
                        columns: x => new { x.TenantId, x.ReportId },
                        principalSchema: "core",
                        principalTable: "Reports",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportShares",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportShares", x => x.Id);
                    table.UniqueConstraint("AK_ReportShares_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReportShares_Reports_TenantId_ReportId",
                        columns: x => new { x.TenantId, x.ReportId },
                        principalSchema: "core",
                        principalTable: "Reports",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportFavorites_TenantId_ReportId_UserId",
                schema: "core",
                table: "ReportFavorites",
                columns: new[] { "TenantId", "ReportId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportFavorites_TenantId_UserId",
                schema: "core",
                table: "ReportFavorites",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TenantId_OwnerUserId_Name",
                schema: "core",
                table: "Reports",
                columns: new[] { "TenantId", "OwnerUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reports_TenantId_Visibility",
                schema: "core",
                table: "Reports",
                columns: new[] { "TenantId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportShares_TenantId_ReportId_UserId",
                schema: "core",
                table: "ReportShares",
                columns: new[] { "TenantId", "ReportId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportShares_TenantId_UserId",
                schema: "core",
                table: "ReportShares",
                columns: new[] { "TenantId", "UserId" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "Reports");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportShares");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportFavorites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportFavorites");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportShares");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "Reports");

            migrationBuilder.DropTable(
                name: "ReportFavorites",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ReportShares",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Reports",
                schema: "core");
        }
    }
}
