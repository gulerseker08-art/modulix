using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportRuns",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Policy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRuns", x => x.Id);
                    table.UniqueConstraint("AK_ImportRuns_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ImportRowIssues",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowNo = table.Column<int>(type: "int", nullable: false),
                    ColumnKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRowIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRowIssues_ImportRuns_TenantId_RunId",
                        columns: x => new { x.TenantId, x.RunId },
                        principalSchema: "core",
                        principalTable: "ImportRuns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowIssues_TenantId_CreatedAt",
                schema: "core",
                table: "ImportRowIssues",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowIssues_TenantId_RunId_Code",
                schema: "core",
                table: "ImportRowIssues",
                columns: new[] { "TenantId", "RunId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowIssues_TenantId_RunId_RowNo",
                schema: "core",
                table: "ImportRowIssues",
                columns: new[] { "TenantId", "RunId", "RowNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_TenantId_CreatedAt",
                schema: "core",
                table: "ImportRuns",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_TenantId_EntityKey_CreatedAt",
                schema: "core",
                table: "ImportRuns",
                columns: new[] { "TenantId", "EntityKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_TenantId_FileHash_CreatedAt",
                schema: "core",
                table: "ImportRuns",
                columns: new[] { "TenantId", "FileHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_TenantId_Status_CreatedAt",
                schema: "core",
                table: "ImportRuns",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRuns_TenantId_UserId_CreatedAt",
                schema: "core",
                table: "ImportRuns",
                columns: new[] { "TenantId", "UserId", "CreatedAt" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ImportRuns");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ImportRowIssues");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ImportRowIssues");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ImportRuns");

            migrationBuilder.DropTable(
                name: "ImportRowIssues",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ImportRuns",
                schema: "core");
        }
    }
}
