using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForecastSubmissions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    ScopeType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ScopeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReportingCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    PreviousSubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReopenReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReturnReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RealizedAtLock = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RealizedPartial = table.Column<bool>(type: "bit", nullable: false),
                    RefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastSubmissions", x => x.Id);
                    table.UniqueConstraint("AK_ForecastSubmissions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ForecastSubmissions_Locked", "[Status] <> 'Locked' OR [LockedAt] IS NOT NULL");
                    table.CheckConstraint("CK_ForecastSubmissions_Period", "[PeriodEnd] >= [PeriodStart]");
                    table.CheckConstraint("CK_ForecastSubmissions_Scope", "([ScopeType] = 'Tenant' AND [ScopeUserId] IS NULL) OR ([ScopeType] = 'User' AND [ScopeUserId] IS NOT NULL)");
                    table.CheckConstraint("CK_ForecastSubmissions_Status", "[Status] IN ('Draft','Submitted','Locked')");
                });

            migrationBuilder.CreateTable(
                name: "SalesTargets",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ScopeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTargets", x => x.Id);
                    table.UniqueConstraint("AK_SalesTargets_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SalesTargets_Currency", "([Metric] = 'WonAmount' AND [Currency] IS NOT NULL) OR ([Metric] = 'WonCount' AND [Currency] IS NULL)");
                    table.CheckConstraint("CK_SalesTargets_Metric", "[Metric] IN ('WonAmount','WonCount')");
                    table.CheckConstraint("CK_SalesTargets_Period", "[PeriodEnd] >= [PeriodStart]");
                    table.CheckConstraint("CK_SalesTargets_Scope", "([ScopeType] = 'Tenant' AND [ScopeUserId] IS NULL) OR ([ScopeType] = 'User' AND [ScopeUserId] IS NOT NULL)");
                    table.CheckConstraint("CK_SalesTargets_Value", "[Value] > 0");
                });

            migrationBuilder.CreateTable(
                name: "ForecastAdjustments",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    FromValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ToValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastAdjustments", x => x.Id);
                    table.UniqueConstraint("AK_ForecastAdjustments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ForecastAdjustments_Kind", "[Kind] IN ('Category','Amount','Probability')");
                    table.ForeignKey(
                        name: "FK_ForecastAdjustments_ForecastSubmissions_TenantId_SubmissionId",
                        columns: x => new { x.TenantId, x.SubmissionId },
                        principalSchema: "crm",
                        principalTable: "ForecastSubmissions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForecastItems",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    SourceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Probability = table.Column<int>(type: "int", nullable: true),
                    ProbabilityManual = table.Column<bool>(type: "bit", nullable: false),
                    ExpectedCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RateUsed = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RateMissing = table.Column<bool>(type: "bit", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BaseCategory = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AdjustmentAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ProbabilityOverride = table.Column<int>(type: "int", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastItems", x => x.Id);
                    table.UniqueConstraint("AK_ForecastItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ForecastItems_BaseCategory", "[BaseCategory] IN ('Omitted','Pipeline','BestCase','Commit')");
                    table.CheckConstraint("CK_ForecastItems_Category", "[Category] IN ('Omitted','Pipeline','BestCase','Commit')");
                    table.CheckConstraint("CK_ForecastItems_Probability", "([Probability] IS NULL OR [Probability] BETWEEN 0 AND 100) AND ([ProbabilityOverride] IS NULL OR [ProbabilityOverride] BETWEEN 0 AND 100)");
                    table.ForeignKey(
                        name: "FK_ForecastItems_ForecastSubmissions_TenantId_SubmissionId",
                        columns: x => new { x.TenantId, x.SubmissionId },
                        principalSchema: "crm",
                        principalTable: "ForecastSubmissions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesTargetRevisions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    OldValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTargetRevisions", x => x.Id);
                    table.UniqueConstraint("AK_SalesTargetRevisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SalesTargetRevisions_SalesTargets_TenantId_TargetId",
                        columns: x => new { x.TenantId, x.TargetId },
                        principalSchema: "crm",
                        principalTable: "SalesTargets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastAdjustments_TenantId_SubmissionId_OpportunityId",
                schema: "crm",
                table: "ForecastAdjustments",
                columns: new[] { "TenantId", "SubmissionId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastItems_TenantId_SubmissionId_OpportunityId",
                schema: "crm",
                table: "ForecastItems",
                columns: new[] { "TenantId", "SubmissionId", "OpportunityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastSubmissions_TenantId_Status",
                schema: "crm",
                table: "ForecastSubmissions",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ForecastSubmissions_Open",
                schema: "crm",
                table: "ForecastSubmissions",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd", "ScopeType", "ScopeUserId", "ReportingCurrency" },
                unique: true,
                filter: "[Status] <> 'Locked'");

            migrationBuilder.CreateIndex(
                name: "UX_ForecastSubmissions_Version",
                schema: "crm",
                table: "ForecastSubmissions",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd", "ScopeType", "ScopeUserId", "ReportingCurrency", "VersionNo" },
                unique: true,
                filter: "[ScopeUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTargetRevisions_TenantId_TargetId_RevisionNo",
                schema: "crm",
                table: "SalesTargetRevisions",
                columns: new[] { "TenantId", "TargetId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTargets_TenantId_PeriodStart_PeriodEnd",
                schema: "crm",
                table: "SalesTargets",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "UX_SalesTargets_Slot",
                schema: "crm",
                table: "SalesTargets",
                columns: new[] { "TenantId", "Metric", "ScopeType", "ScopeUserId", "Currency", "PeriodStart" },
                unique: true,
                filter: "[ScopeUserId] IS NOT NULL AND [Currency] IS NOT NULL");
            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "SalesTargets");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "SalesTargetRevisions");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ForecastSubmissions");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ForecastItems");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ForecastAdjustments");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ForecastAdjustments");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ForecastItems");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "SalesTargetRevisions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ForecastSubmissions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "SalesTargets");

            migrationBuilder.DropTable(
                name: "ForecastAdjustments",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ForecastItems",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "SalesTargetRevisions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ForecastSubmissions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "SalesTargets",
                schema: "crm");
        }
    }
}
