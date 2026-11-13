using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseClaims",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApproverUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PaidByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseClaims", x => x.Id);
                    table.UniqueConstraint("AK_ExpenseClaims_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ExpenseClaims_Employees_TenantId_EmployeeId",
                        columns: x => new { x.TenantId, x.EmployeeId },
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseLines",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpenseTypeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseLines", x => x.Id);
                    table.UniqueConstraint("AK_ExpenseLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ExpenseLines_ExpenseClaims_TenantId_ClaimId",
                        columns: x => new { x.TenantId, x.ClaimId },
                        principalSchema: "hr",
                        principalTable: "ExpenseClaims",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Approver",
                schema: "hr",
                table: "ExpenseClaims",
                columns: new[] { "TenantId", "ApproverUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Employee",
                schema: "hr",
                table: "ExpenseClaims",
                columns: new[] { "TenantId", "EmployeeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Owner",
                schema: "hr",
                table: "ExpenseClaims",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Status",
                schema: "hr",
                table: "ExpenseClaims",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLines_Claim",
                schema: "hr",
                table: "ExpenseLines",
                columns: new[] { "TenantId", "ClaimId", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseLines_Owner",
                schema: "hr",
                table: "ExpenseLines",
                columns: new[] { "TenantId", "OwnerId" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "ExpenseClaims");
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "ExpenseLines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "ExpenseLines");
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "ExpenseClaims");

            migrationBuilder.DropTable(
                name: "ExpenseLines",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "ExpenseClaims",
                schema: "hr");
        }
    }
}
