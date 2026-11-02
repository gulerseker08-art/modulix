using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UtmCampaign",
                schema: "marketing",
                table: "Campaigns",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmMedium",
                schema: "marketing",
                table: "Campaigns",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UtmSource",
                schema: "marketing",
                table: "Campaigns",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CampaignResponses",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OptOutAppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_CampaignResponses", x => x.Id);
                    table.UniqueConstraint("AK_CampaignResponses_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CampaignResponses_CampaignRuns_TenantId_RunId",
                        columns: x => new { x.TenantId, x.RunId },
                        principalSchema: "marketing",
                        principalTable: "CampaignRuns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignResponses_Campaigns_TenantId_CampaignId",
                        columns: x => new { x.TenantId, x.CampaignId },
                        principalSchema: "marketing",
                        principalTable: "Campaigns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignResponses_TenantId_CampaignId_Status",
                schema: "marketing",
                table: "CampaignResponses",
                columns: new[] { "TenantId", "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignResponses_TenantId_DeliveryId",
                schema: "marketing",
                table: "CampaignResponses",
                columns: new[] { "TenantId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignResponses_TenantId_OwnerId_Status",
                schema: "marketing",
                table: "CampaignResponses",
                columns: new[] { "TenantId", "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignResponses_TenantId_RunId",
                schema: "marketing",
                table: "CampaignResponses",
                columns: new[] { "TenantId", "RunId" });

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "marketing", "CampaignResponses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "marketing", "CampaignResponses");

            migrationBuilder.DropTable(
                name: "CampaignResponses",
                schema: "marketing");

            migrationBuilder.DropColumn(
                name: "UtmCampaign",
                schema: "marketing",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "UtmMedium",
                schema: "marketing",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "UtmSource",
                schema: "marketing",
                table: "Campaigns");
        }
    }
}
