using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportScheduleRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportScheduleRecipients",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportScheduleRecipients", x => x.Id);
                    table.UniqueConstraint("AK_ReportScheduleRecipients_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReportScheduleRecipients_ReportSchedules_TenantId_ScheduleId",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalSchema: "core",
                        principalTable: "ReportSchedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportScheduleRecipients_TenantId_ScheduleId_UserId",
                schema: "core",
                table: "ReportScheduleRecipients",
                columns: new[] { "TenantId", "ScheduleId", "UserId" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "core", "ReportScheduleRecipients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ReportScheduleRecipients");

            migrationBuilder.DropTable(
                name: "ReportScheduleRecipients",
                schema: "core");
        }
    }
}
