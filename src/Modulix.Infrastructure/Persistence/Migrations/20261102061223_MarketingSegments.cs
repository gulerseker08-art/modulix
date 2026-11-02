using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "marketing");

            migrationBuilder.CreateTable(
                name: "Segments",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Target = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Draft = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishedVersion = table.Column<int>(type: "int", nullable: false),
                    LastComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastMemberCount = table.Column<int>(type: "int", nullable: true),
                    LastComputeMs = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segments", x => x.Id);
                    table.UniqueConstraint("AK_Segments_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SegmentMembers",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentMembers", x => x.Id);
                    table.UniqueConstraint("AK_SegmentMembers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SegmentMembers_Segments_TenantId_SegmentId",
                        columns: x => new { x.TenantId, x.SegmentId },
                        principalSchema: "marketing",
                        principalTable: "Segments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SegmentVersions",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Definition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentVersions", x => x.Id);
                    table.UniqueConstraint("AK_SegmentVersions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SegmentVersions_Segments_TenantId_SegmentId",
                        columns: x => new { x.TenantId, x.SegmentId },
                        principalSchema: "marketing",
                        principalTable: "Segments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SegmentMembers_Target",
                schema: "marketing",
                table: "SegmentMembers",
                columns: new[] { "TenantId", "SegmentId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Segments_TenantId_Status_Target",
                schema: "marketing",
                table: "Segments",
                columns: new[] { "TenantId", "Status", "Target" });

            migrationBuilder.CreateIndex(
                name: "UX_Segments_Code",
                schema: "marketing",
                table: "Segments",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SegmentVersions_No",
                schema: "marketing",
                table: "SegmentVersions",
                columns: new[] { "TenantId", "SegmentId", "VersionNo" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "marketing", table);
            }

            // Uygulama kullanıcısı yeni şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[marketing] TO [modulix_app_role];");
        }

        private static readonly string[] TenantTables = ["Segments", "SegmentVersions", "SegmentMembers"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "marketing", table);
            }

            migrationBuilder.DropTable(
                name: "SegmentMembers",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "SegmentVersions",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "Segments",
                schema: "marketing");
        }
    }
}
