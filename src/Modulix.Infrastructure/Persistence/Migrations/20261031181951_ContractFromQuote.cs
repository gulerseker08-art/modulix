using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractFromQuote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceOpportunityId",
                schema: "contracts",
                table: "Contracts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceQuoteId",
                schema: "contracts",
                table: "Contracts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceQuoteNumber",
                schema: "contracts",
                table: "Contracts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceQuoteRevisionNo",
                schema: "contracts",
                table: "Contracts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceQuoteSha256",
                schema: "contracts",
                table: "Contracts",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceQuoteId",
                schema: "contracts",
                table: "ContractLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_SourceOpportunityId",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "SourceOpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_SourceQuoteId",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "SourceQuoteId" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractLines_SourceQuoteLine",
                schema: "contracts",
                table: "ContractLines",
                columns: new[] { "TenantId", "SourceQuoteId", "SourceQuoteLineKey" },
                unique: true,
                filter: "[SourceQuoteId] IS NOT NULL AND [SourceQuoteLineKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contracts_TenantId_SourceOpportunityId",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_TenantId_SourceQuoteId",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "UX_ContractLines_SourceQuoteLine",
                schema: "contracts",
                table: "ContractLines");

            migrationBuilder.DropColumn(
                name: "SourceOpportunityId",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SourceQuoteId",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SourceQuoteNumber",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SourceQuoteRevisionNo",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SourceQuoteSha256",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SourceQuoteId",
                schema: "contracts",
                table: "ContractLines");
        }
    }
}
