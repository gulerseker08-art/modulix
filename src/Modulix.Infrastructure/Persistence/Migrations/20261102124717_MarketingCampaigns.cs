using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Target = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecipientRule = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecipientRole = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MaxPerTarget = table.Column<int>(type: "int", nullable: true),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.UniqueConstraint("AK_Campaigns_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "AudienceSnapshots",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TakenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TakenBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetCount = table.Column<int>(type: "int", nullable: false),
                    ResolvedTargetCount = table.Column<int>(type: "int", nullable: false),
                    PersonCount = table.Column<int>(type: "int", nullable: false),
                    UniqueAddressCount = table.Column<int>(type: "int", nullable: false),
                    EligibleCount = table.Column<int>(type: "int", nullable: false),
                    ExcludedCount = table.Column<int>(type: "int", nullable: false),
                    Exclusions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Unresolved = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sources = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudienceSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_AudienceSnapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AudienceSnapshots_Campaigns_TenantId_CampaignId",
                        columns: x => new { x.TenantId, x.CampaignId },
                        principalSchema: "marketing",
                        principalTable: "Campaigns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CampaignAudienceEntries",
                schema: "marketing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignAudienceEntries", x => x.Id);
                    table.UniqueConstraint("AK_CampaignAudienceEntries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_CampaignAudience_OneSource", "([SegmentId] IS NULL AND [TargetId] IS NOT NULL) OR ([SegmentId] IS NOT NULL AND [TargetId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_CampaignAudienceEntries_Campaigns_TenantId_CampaignId",
                        columns: x => new { x.TenantId, x.CampaignId },
                        principalSchema: "marketing",
                        principalTable: "Campaigns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CampaignAudienceEntries_Segments_TenantId_SegmentId",
                        columns: x => new { x.TenantId, x.SegmentId },
                        principalSchema: "marketing",
                        principalTable: "Segments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudienceSnapshots_TenantId_CampaignId_TakenAt",
                schema: "marketing",
                table: "AudienceSnapshots",
                columns: new[] { "TenantId", "CampaignId", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignAudienceEntries_TenantId_SegmentId",
                schema: "marketing",
                table: "CampaignAudienceEntries",
                columns: new[] { "TenantId", "SegmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_CampaignAudience_Segment",
                schema: "marketing",
                table: "CampaignAudienceEntries",
                columns: new[] { "TenantId", "CampaignId", "SegmentId" },
                unique: true,
                filter: "[SegmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CampaignAudience_Target",
                schema: "marketing",
                table: "CampaignAudienceEntries",
                columns: new[] { "TenantId", "CampaignId", "TargetId" },
                unique: true,
                filter: "[TargetId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_TenantId_Status",
                schema: "marketing",
                table: "Campaigns",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Campaigns_Code",
                schema: "marketing",
                table: "Campaigns",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "marketing", table);
            }
        }

        private static readonly string[] TenantTables = ["Campaigns", "CampaignAudienceEntries", "AudienceSnapshots"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "marketing", table);
            }

            migrationBuilder.DropTable(
                name: "AudienceSnapshots",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "CampaignAudienceEntries",
                schema: "marketing");

            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "marketing");
        }
    }
}
