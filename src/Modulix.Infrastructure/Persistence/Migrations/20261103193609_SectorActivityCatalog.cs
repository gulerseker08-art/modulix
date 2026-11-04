using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SectorActivityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityNodes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Readiness = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityNodes_ActivityNodes_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "platform",
                        principalTable: "ActivityNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Capabilities",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Capabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivitySelections",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySelections", x => x.Id);
                    table.UniqueConstraint("AK_ActivitySelections_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ActivitySelections_ActivityNodes_NodeId",
                        column: x => x.NodeId,
                        principalSchema: "platform",
                        principalTable: "ActivityNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityNodeCapabilities",
                schema: "platform",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapabilityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityNodeCapabilities", x => new { x.NodeId, x.CapabilityId });
                    table.ForeignKey(
                        name: "FK_ActivityNodeCapabilities_ActivityNodes_NodeId",
                        column: x => x.NodeId,
                        principalSchema: "platform",
                        principalTable: "ActivityNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityNodeCapabilities_Capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "platform",
                        principalTable: "Capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CapabilityDependencies",
                schema: "platform",
                columns: table => new
                {
                    CapabilityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DependsOnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapabilityDependencies", x => new { x.CapabilityId, x.DependsOnId });
                    table.ForeignKey(
                        name: "FK_CapabilityDependencies_Capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "platform",
                        principalTable: "Capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CapabilityDependencies_Capabilities_DependsOnId",
                        column: x => x.DependsOnId,
                        principalSchema: "platform",
                        principalTable: "Capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityNodeCapabilities_CapabilityId",
                schema: "platform",
                table: "ActivityNodeCapabilities",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityNodes_Code",
                schema: "platform",
                table: "ActivityNodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityNodes_ParentId",
                schema: "platform",
                table: "ActivityNodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySelections_NodeId",
                schema: "core",
                table: "ActivitySelections",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySelections_TenantId_NodeId",
                schema: "core",
                table: "ActivitySelections",
                columns: new[] { "TenantId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Capabilities_Code",
                schema: "platform",
                table: "Capabilities",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityDependencies_DependsOnId",
                schema: "platform",
                table: "CapabilityDependencies",
                column: "DependsOnId");

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            // Katalog tabloları (platform şeması, TenantId yok) bilerek RLS dışındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ActivitySelections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ActivitySelections");

            migrationBuilder.DropTable(
                name: "ActivityNodeCapabilities",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ActivitySelections",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CapabilityDependencies",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ActivityNodes",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Capabilities",
                schema: "platform");
        }
    }
}
