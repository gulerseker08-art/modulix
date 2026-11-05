using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuleDefinitionPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuleAdaptations",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisabledKeysJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleAdaptations", x => x.Id);
                    table.UniqueConstraint("AK_RuleAdaptations_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "RulePackages",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Layer = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulePackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RulePackages_ActivityNodes_NodeId",
                        column: x => x.NodeId,
                        principalSchema: "platform",
                        principalTable: "ActivityNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RulePackageVersions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulePackageVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RulePackageVersions_RulePackages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "platform",
                        principalTable: "RulePackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuleAdaptations_TenantId",
                schema: "core",
                table: "RuleAdaptations",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RulePackages_Code",
                schema: "platform",
                table: "RulePackages",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RulePackages_NodeId",
                schema: "platform",
                table: "RulePackages",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_RulePackageVersions_PackageId_Number",
                schema: "platform",
                table: "RulePackageVersions",
                columns: new[] { "PackageId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RulePackageVersions_Draft",
                schema: "platform",
                table: "RulePackageVersions",
                column: "PackageId",
                unique: true,
                filter: "[Status] = N'Draft'");

            migrationBuilder.CreateIndex(
                name: "UX_RulePackageVersions_Published",
                schema: "platform",
                table: "RulePackageVersions",
                column: "PackageId",
                unique: true,
                filter: "[Status] = N'Published'");

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            // Paket tabloları (platform şeması, TenantId yok) bilerek RLS dışındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "core", "RuleAdaptations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "RuleAdaptations");

            migrationBuilder.DropTable(
                name: "RuleAdaptations",
                schema: "core");

            migrationBuilder.DropTable(
                name: "RulePackageVersions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "RulePackages",
                schema: "platform");
        }
    }
}
