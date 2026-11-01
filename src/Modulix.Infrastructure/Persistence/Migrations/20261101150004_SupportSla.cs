using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstResponseDueAt",
                schema: "support",
                table: "Tickets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionDueAt",
                schema: "support",
                table: "Tickets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlaCalendars",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsAlwaysOpen = table.Column<bool>(type: "bit", nullable: false),
                    WeeklyHours = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Holidays = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_SlaCalendars", x => x.Id);
                    table.UniqueConstraint("AK_SlaCalendars_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Precedence = table.Column<int>(type: "int", nullable: false),
                    TypeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: true),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PauseOnWaitingCustomer = table.Column<bool>(type: "bit", nullable: false),
                    PauseOnWaitingInternal = table.Column<bool>(type: "bit", nullable: false),
                    EscalationRules = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.UniqueConstraint("AK_SlaPolicies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SlaPolicies_Priority", "[Priority] IS NULL OR [Priority] IN ('Low','Normal','High','Critical')");
                    table.CheckConstraint("CK_SlaPolicies_Targets", "([FirstResponseMinutes] IS NOT NULL OR [ResolutionMinutes] IS NOT NULL) AND ([FirstResponseMinutes] IS NULL OR [FirstResponseMinutes] > 0) AND ([ResolutionMinutes] IS NULL OR [ResolutionMinutes] > 0)");
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Categories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "support",
                        principalTable: "Categories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_SlaCalendars_TenantId_CalendarId",
                        columns: x => new { x.TenantId, x.CalendarId },
                        principalSchema: "support",
                        principalTable: "SlaCalendars",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaInstances",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyRevision = table.Column<int>(type: "int", nullable: false),
                    PolicyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Snapshot = table.Column<string>(type: "nvarchar(max)", maxLength: 40000, nullable: false),
                    TargetMinutes = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RunningSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WorkedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WarningAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BreachedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaInstances", x => x.Id);
                    table.UniqueConstraint("AK_SlaInstances_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SlaInstances_Metric", "[Metric] IN ('FirstResponse','Resolution')");
                    table.CheckConstraint("CK_SlaInstances_Status", "[Status] IN ('Running','Paused','Completed','Cancelled')");
                    table.CheckConstraint("CK_SlaInstances_Target", "[TargetMinutes] > 0 AND [WorkedSeconds] >= 0");
                    table.ForeignKey(
                        name: "FK_SlaInstances_SlaPolicies_TenantId_PolicyId",
                        columns: x => new { x.TenantId, x.PolicyId },
                        principalSchema: "support",
                        principalTable: "SlaPolicies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaInstances_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalSchema: "support",
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaEscalations",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Percent = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ThresholdAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaEscalations", x => x.Id);
                    table.UniqueConstraint("AK_SlaEscalations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SlaEscalations_SlaInstances_TenantId_InstanceId",
                        columns: x => new { x.TenantId, x.InstanceId },
                        principalSchema: "support",
                        principalTable: "SlaInstances",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaPauses",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPauses", x => x.Id);
                    table.UniqueConstraint("AK_SlaPauses_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SlaPauses_SlaInstances_TenantId_InstanceId",
                        columns: x => new { x.TenantId, x.InstanceId },
                        principalSchema: "support",
                        principalTable: "SlaInstances",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SlaCalendars_Name",
                schema: "support",
                table: "SlaCalendars",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaEscalations_TenantId_TicketId",
                schema: "support",
                table: "SlaEscalations",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "UX_SlaEscalations_Rule",
                schema: "support",
                table: "SlaEscalations",
                columns: new[] { "TenantId", "InstanceId", "RuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaInstances_TenantId_PolicyId",
                schema: "support",
                table: "SlaInstances",
                columns: new[] { "TenantId", "PolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaInstances_TenantId_Status_DueAt",
                schema: "support",
                table: "SlaInstances",
                columns: new[] { "TenantId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaInstances_TenantId_Status_WarningAt",
                schema: "support",
                table: "SlaInstances",
                columns: new[] { "TenantId", "Status", "WarningAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlaInstances_Cycle",
                schema: "support",
                table: "SlaInstances",
                columns: new[] { "TenantId", "TicketId", "Cycle", "Metric" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaPauses_TenantId_InstanceId_StartedAt",
                schema: "support",
                table: "SlaPauses",
                columns: new[] { "TenantId", "InstanceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_TenantId_CalendarId",
                schema: "support",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "CalendarId" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_TenantId_CategoryId",
                schema: "support",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_TenantId_PartyId",
                schema: "support",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "UX_SlaPolicies_Default",
                schema: "support",
                table: "SlaPolicies",
                column: "TenantId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_SlaPolicies_Name",
                schema: "support",
                table: "SlaPolicies",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema yetkisi ilk destek migration'ında verildi.
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "support", table);
            }
        }

        private static readonly string[] TenantTables = ["SlaCalendars", "SlaPolicies", "SlaInstances", "SlaPauses", "SlaEscalations"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "support", table);
            }

            migrationBuilder.DropTable(
                name: "SlaEscalations",
                schema: "support");

            migrationBuilder.DropTable(
                name: "SlaPauses",
                schema: "support");

            migrationBuilder.DropTable(
                name: "SlaInstances",
                schema: "support");

            migrationBuilder.DropTable(
                name: "SlaPolicies",
                schema: "support");

            migrationBuilder.DropTable(
                name: "SlaCalendars",
                schema: "support");

            migrationBuilder.DropColumn(
                name: "FirstResponseDueAt",
                schema: "support",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                schema: "support",
                table: "Tickets");
        }
    }
}
