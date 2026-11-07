using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalDefinitions",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StepsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDefinitions", x => x.Id);
                    table.UniqueConstraint("AK_ApprovalDefinitions_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DefinitionRevision = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.UniqueConstraint("AK_ApprovalRequests_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_ApprovalDefinitions_TenantId_DefinitionId",
                        columns: x => new { x.TenantId, x.DefinitionId },
                        principalSchema: "core",
                        principalTable: "ApprovalDefinitions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequestSteps",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepOrder = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ApproverUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApproverRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueHours = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequestSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalRequestSteps_ApprovalRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "core",
                        principalTable: "ApprovalRequests",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDefinitions_TenantId_Name",
                schema: "core",
                table: "ApprovalDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_DefinitionId",
                schema: "core",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "DefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_RequesterId_Status",
                schema: "core",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TenantId_Status_SubmittedAt",
                schema: "core",
                table: "ApprovalRequests",
                columns: new[] { "TenantId", "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequestSteps_TenantId_RequestId_StepOrder",
                schema: "core",
                table: "ApprovalRequestSteps",
                columns: new[] { "TenantId", "RequestId", "StepOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequestSteps_TenantId_Status_ApproverRoleId",
                schema: "core",
                table: "ApprovalRequestSteps",
                columns: new[] { "TenantId", "Status", "ApproverRoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequestSteps_TenantId_Status_ApproverUserId",
                schema: "core",
                table: "ApprovalRequestSteps",
                columns: new[] { "TenantId", "Status", "ApproverUserId" });

            TenantIsolationSql.AddTable(migrationBuilder, "core", "ApprovalDefinitions");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ApprovalRequests");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ApprovalRequestSteps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ApprovalRequestSteps");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ApprovalRequests");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ApprovalDefinitions");

            migrationBuilder.DropTable(
                name: "ApprovalRequestSteps",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ApprovalRequests",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ApprovalDefinitions",
                schema: "core");
        }
    }
}
