using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    InitiatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                    table.UniqueConstraint("AK_AutomationRuns_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AutomationRuns_AutomationRuleVersions_TenantId_RuleVersionId",
                        columns: x => new { x.TenantId, x.RuleVersionId },
                        principalSchema: "core",
                        principalTable: "AutomationRuleVersions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRunSteps",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepNo = table.Column<int>(type: "int", nullable: false),
                    StepId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EffectEntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EffectRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRunSteps_AutomationRuns_TenantId_RunId",
                        columns: x => new { x.TenantId, x.RunId },
                        principalSchema: "core",
                        principalTable: "AutomationRuns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_DedupeKey",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_EntityKey_RecordId",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "EntityKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_RuleId_CreatedAt",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "RuleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_RuleId_RecordId",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "RuleId", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_RuleVersionId",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "RuleVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_TenantId_Status_NextAttemptAt",
                schema: "core",
                table: "AutomationRuns",
                columns: new[] { "TenantId", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRunSteps_TenantId_RunId_StepNo",
                schema: "core",
                table: "AutomationRunSteps",
                columns: new[] { "TenantId", "RunId", "StepNo" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "AutomationRuns");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "AutomationRunSteps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "AutomationRunSteps");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "AutomationRuns");

            migrationBuilder.DropTable(
                name: "AutomationRunSteps",
                schema: "core");

            migrationBuilder.DropTable(
                name: "AutomationRuns",
                schema: "core");
        }
    }
}
