using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovalDueAt",
                schema: "contracts",
                table: "ContractVersions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalPlan",
                schema: "contracts",
                table: "ContractVersions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalRound",
                schema: "contracts",
                table: "ContractVersions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasNonStandardTerms",
                schema: "contracts",
                table: "Contracts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                schema: "contracts",
                table: "Contracts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Low");

            migrationBuilder.CreateTable(
                name: "ContractApprovalDecisions",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    StepNo = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnBehalfOf = table.Column<string>(type: "varchar(400)", unicode: false, maxLength: 400, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractApprovalDecisions", x => x.Id);
                    table.UniqueConstraint("AK_ContractApprovalDecisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractApprovalDecisions_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractApprovalDelegations",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractApprovalDelegations", x => x.Id);
                    table.UniqueConstraint("AK_ContractApprovalDelegations_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ContractApprovalRules",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ContractType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    MinGrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MinDiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    MinTermMonths = table.Column<int>(type: "int", nullable: true),
                    RequiresNonStandardTerms = table.Column<bool>(type: "bit", nullable: false),
                    MinRiskLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ApproverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractApprovalRules", x => x.Id);
                    table.UniqueConstraint("AK_ContractApprovalRules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ContractApprovalRules_Step", "[Step] BETWEEN 1 AND 9");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractApprovalDecisions_TenantId_ContractId_VersionNo_Round_CreatedAt",
                schema: "contracts",
                table: "ContractApprovalDecisions",
                columns: new[] { "TenantId", "ContractId", "VersionNo", "Round", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractApprovalDelegations_TenantId_FromUserId_IsActive",
                schema: "contracts",
                table: "ContractApprovalDelegations",
                columns: new[] { "TenantId", "FromUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractApprovalDelegations_TenantId_ToUserId_IsActive",
                schema: "contracts",
                table: "ContractApprovalDelegations",
                columns: new[] { "TenantId", "ToUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractApprovalRules_TenantId_IsActive_Step_Priority",
                schema: "contracts",
                table: "ContractApprovalRules",
                columns: new[] { "TenantId", "IsActive", "Step", "Priority" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "contracts", table);
            }
        }

        private static readonly string[] TenantTables = ["ContractApprovalRules", "ContractApprovalDelegations", "ContractApprovalDecisions"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "contracts", table);
            }

            migrationBuilder.DropTable(
                name: "ContractApprovalDecisions",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractApprovalDelegations",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractApprovalRules",
                schema: "contracts");

            migrationBuilder.DropColumn(
                name: "ApprovalDueAt",
                schema: "contracts",
                table: "ContractVersions");

            migrationBuilder.DropColumn(
                name: "ApprovalPlan",
                schema: "contracts",
                table: "ContractVersions");

            migrationBuilder.DropColumn(
                name: "ApprovalRound",
                schema: "contracts",
                table: "ContractVersions");

            migrationBuilder.DropColumn(
                name: "HasNonStandardTerms",
                schema: "contracts",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                schema: "contracts",
                table: "Contracts");
        }
    }
}
