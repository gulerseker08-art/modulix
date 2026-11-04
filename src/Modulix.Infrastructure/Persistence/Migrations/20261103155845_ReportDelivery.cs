using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportExports",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReportName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DatasetKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Truncated = table.Column<bool>(type: "bit", nullable: false),
                    AccessStamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DownloadCount = table.Column<int>(type: "int", nullable: false),
                    LastDownloadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportExports", x => x.Id);
                    table.UniqueConstraint("AK_ReportExports_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Frequency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Hour = table.Column<int>(type: "int", nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastMessage = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StoppedReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedules", x => x.Id);
                    table.UniqueConstraint("AK_ReportSchedules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReportSchedules_Reports_TenantId_ReportId",
                        columns: x => new { x.TenantId, x.ReportId },
                        principalSchema: "core",
                        principalTable: "Reports",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportScheduleRuns",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slot = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExportId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportScheduleRuns", x => x.Id);
                    table.UniqueConstraint("AK_ReportScheduleRuns_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReportScheduleRuns_ReportSchedules_TenantId_ScheduleId",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalSchema: "core",
                        principalTable: "ReportSchedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExports_TenantId_ExpiresAt",
                schema: "core",
                table: "ReportExports",
                columns: new[] { "TenantId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExports_TenantId_OwnerUserId_CreatedAt",
                schema: "core",
                table: "ReportExports",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportScheduleRuns_TenantId_ScheduleId_Slot",
                schema: "core",
                table: "ReportScheduleRuns",
                columns: new[] { "TenantId", "ScheduleId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_TenantId_NextRunAt_Enabled",
                schema: "core",
                table: "ReportSchedules",
                columns: new[] { "TenantId", "NextRunAt" },
                filter: "[Enabled] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_TenantId_OwnerUserId",
                schema: "core",
                table: "ReportSchedules",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_TenantId_ReportId",
                schema: "core",
                table: "ReportSchedules",
                columns: new[] { "TenantId", "ReportId" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportExports");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportSchedules");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportScheduleRuns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportScheduleRuns");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportSchedules");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportExports");

            migrationBuilder.DropTable(
                name: "ReportExports",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ReportScheduleRuns",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ReportSchedules",
                schema: "core");
        }
    }
}
