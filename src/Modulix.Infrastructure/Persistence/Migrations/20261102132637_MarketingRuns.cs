using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignContents",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignContents", x => x.Id);
                    table.UniqueConstraint("AK_CampaignContents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CampaignContents_Campaigns_TenantId_CampaignId",
                        columns: x => new { x.TenantId, x.CampaignId },
                        principalSchema: "marketing",
                        principalTable: "Campaigns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FrequencyWindows",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrequencyWindows", x => x.Id);
                    table.CheckConstraint("CK_FrequencyWindows_Count", "[Count] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "CampaignRuns",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
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
                    table.PrimaryKey("PK_CampaignRuns", x => x.Id);
                    table.UniqueConstraint("AK_CampaignRuns_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CampaignRuns_AudienceSnapshots_TenantId_SnapshotId",
                        columns: x => new { x.TenantId, x.SnapshotId },
                        principalSchema: "marketing",
                        principalTable: "AudienceSnapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRuns_CampaignContents_TenantId_ContentId",
                        columns: x => new { x.TenantId, x.ContentId },
                        principalSchema: "marketing",
                        principalTable: "CampaignContents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignRuns_Campaigns_TenantId_CampaignId",
                        columns: x => new { x.TenantId, x.CampaignId },
                        principalSchema: "marketing",
                        principalTable: "Campaigns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CampaignDeliveries",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignDeliveries_CampaignRuns_TenantId_RunId",
                        columns: x => new { x.TenantId, x.RunId },
                        principalSchema: "marketing",
                        principalTable: "CampaignRuns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CampaignContents_No",
                schema: "marketing",
                table: "CampaignContents",
                columns: new[] { "TenantId", "CampaignId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDeliveries_TenantId_RunId_Status",
                schema: "marketing",
                table: "CampaignDeliveries",
                columns: new[] { "TenantId", "RunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_CampaignDeliveries_Key",
                schema: "marketing",
                table: "CampaignDeliveries",
                columns: new[] { "TenantId", "RunId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRuns_TenantId_CampaignId_Status",
                schema: "marketing",
                table: "CampaignRuns",
                columns: new[] { "TenantId", "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRuns_TenantId_ContentId",
                schema: "marketing",
                table: "CampaignRuns",
                columns: new[] { "TenantId", "ContentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRuns_TenantId_SnapshotId",
                schema: "marketing",
                table: "CampaignRuns",
                columns: new[] { "TenantId", "SnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRuns_TenantId_Status_ScheduledAt",
                schema: "marketing",
                table: "CampaignRuns",
                columns: new[] { "TenantId", "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FrequencyWindows_TenantId_Channel_Address_Day",
                schema: "core",
                table: "FrequencyWindows",
                columns: new[] { "TenantId", "Channel", "Address", "Day" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var (schema, table) in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, schema, table);
            }
        }

        private static readonly (string Schema, string Table)[] TenantTables =
        [
            ("core", "FrequencyWindows"), ("marketing", "CampaignContents"), ("marketing", "CampaignRuns"), ("marketing", "CampaignDeliveries"),
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (schema, table) in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, schema, table);
            }

            migrationBuilder.DropTable(
                name: "CampaignDeliveries",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "FrequencyWindows",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CampaignRuns",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "CampaignContents",
                schema: "marketing");
        }
    }
}
