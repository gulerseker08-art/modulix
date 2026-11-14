using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmCampaignLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                schema: "crm",
                table: "Opportunities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                schema: "crm",
                table: "Leads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_CampaignId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "CampaignId" },
                filter: "[CampaignId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_CampaignId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "CampaignId" },
                filter: "[CampaignId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_CampaignId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Leads_TenantId_CampaignId",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                schema: "crm",
                table: "Leads");
        }
    }
}
