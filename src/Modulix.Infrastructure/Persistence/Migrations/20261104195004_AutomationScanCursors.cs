using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationScanCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationScanCursors",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AfterRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordsEnqueued = table.Column<int>(type: "int", nullable: false),
                    Completed = table.Column<bool>(type: "bit", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationScanCursors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationScanCursors_AutomationRuleVersions_TenantId_RuleVersionId",
                        columns: x => new { x.TenantId, x.RuleVersionId },
                        principalSchema: "core",
                        principalTable: "AutomationRuleVersions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationScanCursors_TenantId_RuleId",
                schema: "core",
                table: "AutomationScanCursors",
                columns: new[] { "TenantId", "RuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationScanCursors_TenantId_RuleVersionId_PeriodKey",
                schema: "core",
                table: "AutomationScanCursors",
                columns: new[] { "TenantId", "RuleVersionId", "PeriodKey" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "AutomationScanCursors");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "AutomationScanCursors");

            migrationBuilder.DropTable(
                name: "AutomationScanCursors",
                schema: "core");
        }
    }
}
