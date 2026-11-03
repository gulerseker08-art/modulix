using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRules",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModuleKey = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.UniqueConstraint("AK_AutomationRules_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuleVersions",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    StopProcessing = table.Column<bool>(type: "bit", nullable: false),
                    ActiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActiveUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuleVersions", x => x.Id);
                    table.UniqueConstraint("AK_AutomationRuleVersions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AutomationRuleVersions_AutomationRules_TenantId_RuleId",
                        columns: x => new { x.TenantId, x.RuleId },
                        principalSchema: "core",
                        principalTable: "AutomationRules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_TenantId_EntityKey_Status",
                schema: "core",
                table: "AutomationRules",
                columns: new[] { "TenantId", "EntityKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_TenantId_ModuleKey_Status",
                schema: "core",
                table: "AutomationRules",
                columns: new[] { "TenantId", "ModuleKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleVersions_TenantId_RuleId_Draft",
                schema: "core",
                table: "AutomationRuleVersions",
                columns: new[] { "TenantId", "RuleId" },
                unique: true,
                filter: "[State] = N'Draft'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleVersions_TenantId_RuleId_VersionNo",
                schema: "core",
                table: "AutomationRuleVersions",
                columns: new[] { "TenantId", "RuleId", "VersionNo" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "AutomationRules");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "AutomationRuleVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "AutomationRuleVersions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "AutomationRules");

            migrationBuilder.DropTable(
                name: "AutomationRuleVersions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "AutomationRules",
                schema: "core");
        }
    }
}
