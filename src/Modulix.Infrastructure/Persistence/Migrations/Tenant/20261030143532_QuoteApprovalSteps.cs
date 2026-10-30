using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class QuoteApprovalSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Step",
                schema: "crm",
                table: "QuoteApprovalRules",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "QuoteApprovalDelegations",
                schema: "crm",
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
                    table.PrimaryKey("PK_QuoteApprovalDelegations", x => x.Id);
                    table.UniqueConstraint("AK_QuoteApprovalDelegations_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_QuoteApprovalRules_Step",
                schema: "crm",
                table: "QuoteApprovalRules",
                sql: "[Step] BETWEEN 1 AND 9");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteApprovalDelegations_TenantId_FromUserId_IsActive",
                schema: "crm",
                table: "QuoteApprovalDelegations",
                columns: new[] { "TenantId", "FromUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteApprovalDelegations_TenantId_ToUserId_IsActive",
                schema: "crm",
                table: "QuoteApprovalDelegations",
                columns: new[] { "TenantId", "ToUserId", "IsActive" });

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteApprovalDelegations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteApprovalDelegations");

            migrationBuilder.DropTable(
                name: "QuoteApprovalDelegations",
                schema: "crm");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QuoteApprovalRules_Step",
                schema: "crm",
                table: "QuoteApprovalRules");

            migrationBuilder.DropColumn(
                name: "Step",
                schema: "crm",
                table: "QuoteApprovalRules");
        }
    }
}
