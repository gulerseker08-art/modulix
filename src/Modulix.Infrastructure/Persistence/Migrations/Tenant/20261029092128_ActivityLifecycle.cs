using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class ActivityLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActualMinutes",
                schema: "crm",
                table: "Activities",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                schema: "crm",
                table: "Activities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndAt",
                schema: "crm",
                table: "Activities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                schema: "crm",
                table: "Activities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstimatedMinutes",
                schema: "crm",
                table: "Activities",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAllDay",
                schema: "crm",
                table: "Activities",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                schema: "crm",
                table: "Activities",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnlineMeetingUrl",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousActivityId",
                schema: "crm",
                table: "Activities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<int>(
                name: "RescheduleCount",
                schema: "crm",
                table: "Activities",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartAt",
                schema: "crm",
                table: "Activities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                schema: "crm",
                table: "Activities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // Mevcut aktivitelerin durumu tamamlanma ve sonuçtan türetilir (ADR-0015): tamamlanmış kayıtlar tamamlanmış kalır, açıklar açık kalır.
            migrationBuilder.Sql("""
                UPDATE [crm].[Activities] SET [Status] = CASE
                    WHEN [CompletedAt] IS NOT NULL AND [Type] = N'Email' THEN N'Sent'
                    WHEN [CompletedAt] IS NOT NULL AND [Outcome] = N'NoAnswer' AND [Type] = N'Call' THEN N'NoAnswer'
                    WHEN [CompletedAt] IS NOT NULL AND [Outcome] = N'NoAnswer' AND [Type] IN (N'Meeting', N'Visit') THEN N'NoShow'
                    WHEN [CompletedAt] IS NOT NULL THEN N'Completed'
                    WHEN [Type] = N'Email' THEN N'Draft'
                    ELSE N'Open' END
                WHERE [Status] = N'';
                """);

            migrationBuilder.CreateTable(
                name: "ActivityLinks",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLinks", x => x.Id);
                    table.UniqueConstraint("AK_ActivityLinks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ActivityLinks_Activities_TenantId_ActivityId",
                        columns: x => new { x.TenantId, x.ActivityId },
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityParticipants",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityParticipants", x => x.Id);
                    table.UniqueConstraint("AK_ActivityParticipants_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ActivityParticipants_Activities_TenantId_ActivityId",
                        columns: x => new { x.TenantId, x.ActivityId },
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityParticipants_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_ExternalSource_ExternalId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "ExternalSource", "ExternalId" },
                unique: true,
                filter: "[ExternalSource] IS NOT NULL AND [ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_OwnerId_Status_DueAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OwnerId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_PreviousActivityId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "PreviousActivityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_StartAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_StartDate",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLinks_TenantId_ActivityId_EntityKey_RecordId",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "ActivityId", "EntityKey", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLinks_TenantId_EntityKey_RecordId",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "EntityKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "UX_ActivityLinks_Primary",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "ActivityId" },
                unique: true,
                filter: "[IsPrimary] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_ContactId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "ContactId" },
                unique: true,
                filter: "[ContactId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_NormalizedEmail",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "NormalizedEmail" },
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_UserId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "UserId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ContactId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_UserId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Activities_TenantId_PreviousActivityId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "PreviousActivityId" },
                principalSchema: "crm",
                principalTable: "Activities",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ActivityLinks");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ActivityParticipants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ActivityLinks");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ActivityParticipants");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Activities_TenantId_PreviousActivityId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropTable(
                name: "ActivityLinks",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ActivityParticipants",
                schema: "crm");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_ExternalSource_ExternalId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_OwnerId_Status_DueAt",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_PreviousActivityId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_StartAt",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_StartDate",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ActualMinutes",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "EndAt",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "EndDate",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "EstimatedMinutes",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "IsAllDay",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Location",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "OnlineMeetingUrl",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "PreviousActivityId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "RescheduleCount",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "StartAt",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "StartDate",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                schema: "crm",
                table: "Activities");
        }
    }
}
