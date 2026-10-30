using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class OpportunityPrimaryQuote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryQuoteId",
                schema: "crm",
                table: "Opportunities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_PrimaryQuoteId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "PrimaryQuoteId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_PrimaryQuoteId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "PrimaryQuoteId",
                schema: "crm",
                table: "Opportunities");
        }
    }
}
