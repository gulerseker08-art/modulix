using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationJobs",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandoverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GroupKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TeamName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PlannedStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PlannedEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ActualStartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActualEndAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UnassignedAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OverdueAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobs", x => x.Id);
                    table.UniqueConstraint("AK_OperationJobs_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_OperationJobs_Dates", "[PlannedEndDate] IS NULL OR [PlannedStartDate] IS NULL OR [PlannedEndDate] >= [PlannedStartDate]");
                    table.CheckConstraint("CK_OperationJobs_Status", "[Status] IN ('AwaitingAssignment','Planned','InProgress','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_OperationJobs_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobDependencies",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DependsOnJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobDependencies", x => x.Id);
                    table.UniqueConstraint("AK_OperationJobDependencies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_OperationJobDependencies_Self", "[JobId] <> [DependsOnJobId]");
                    table.ForeignKey(
                        name: "FK_OperationJobDependencies_OperationJobs_TenantId_DependsOnJobId",
                        columns: x => new { x.TenantId, x.DependsOnJobId },
                        principalSchema: "orders",
                        principalTable: "OperationJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationJobDependencies_OperationJobs_TenantId_JobId",
                        columns: x => new { x.TenantId, x.JobId },
                        principalSchema: "orders",
                        principalTable: "OperationJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobEvents",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobEvents", x => x.Id);
                    table.UniqueConstraint("AK_OperationJobEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OperationJobEvents_OperationJobs_TenantId_JobId",
                        columns: x => new { x.TenantId, x.JobId },
                        principalSchema: "orders",
                        principalTable: "OperationJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobLines",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobLines", x => x.Id);
                    table.UniqueConstraint("AK_OperationJobLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OperationJobLines_OperationJobs_TenantId_JobId",
                        columns: x => new { x.TenantId, x.JobId },
                        principalSchema: "orders",
                        principalTable: "OperationJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationJobLines_OrderLines_TenantId_OrderLineId",
                        columns: x => new { x.TenantId, x.OrderLineId },
                        principalSchema: "orders",
                        principalTable: "OrderLines",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobTasks",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsDone = table.Column<bool>(type: "bit", nullable: false),
                    DoneBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobTasks", x => x.Id);
                    table.UniqueConstraint("AK_OperationJobTasks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OperationJobTasks_OperationJobs_TenantId_JobId",
                        columns: x => new { x.TenantId, x.JobId },
                        principalSchema: "orders",
                        principalTable: "OperationJobs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobDependencies_TenantId_DependsOnJobId",
                schema: "orders",
                table: "OperationJobDependencies",
                columns: new[] { "TenantId", "DependsOnJobId" });

            migrationBuilder.CreateIndex(
                name: "UX_OperationJobDependencies_Pair",
                schema: "orders",
                table: "OperationJobDependencies",
                columns: new[] { "TenantId", "JobId", "DependsOnJobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobEvents_TenantId_JobId_CreatedAt",
                schema: "orders",
                table: "OperationJobEvents",
                columns: new[] { "TenantId", "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobLines_TenantId_OrderId",
                schema: "orders",
                table: "OperationJobLines",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobLines_TenantId_OrderLineId",
                schema: "orders",
                table: "OperationJobLines",
                columns: new[] { "TenantId", "OrderLineId" });

            migrationBuilder.CreateIndex(
                name: "UX_OperationJobLines_Line",
                schema: "orders",
                table: "OperationJobLines",
                columns: new[] { "TenantId", "JobId", "OrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_TenantId_OrderId",
                schema: "orders",
                table: "OperationJobs",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_TenantId_ResponsibleUserId_Status",
                schema: "orders",
                table: "OperationJobs",
                columns: new[] { "TenantId", "ResponsibleUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_TenantId_Status_PlannedEndDate",
                schema: "orders",
                table: "OperationJobs",
                columns: new[] { "TenantId", "Status", "PlannedEndDate" });

            migrationBuilder.CreateIndex(
                name: "UX_OperationJobs_Group",
                schema: "orders",
                table: "OperationJobs",
                columns: new[] { "TenantId", "OrderId", "GroupKey" },
                unique: true,
                filter: "[GroupKey] IS NOT NULL AND [Status] <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "UX_OperationJobs_Number",
                schema: "orders",
                table: "OperationJobs",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobTasks_TenantId_JobId_Position",
                schema: "orders",
                table: "OperationJobTasks",
                columns: new[] { "TenantId", "JobId", "Position" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (şema yetkisi OrderManagement migration'ında verildi).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "orders", table);
            }
        }

        private static readonly string[] TenantTables = ["OperationJobs", "OperationJobLines", "OperationJobTasks", "OperationJobDependencies", "OperationJobEvents"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "orders", table);
            }

            migrationBuilder.DropTable(
                name: "OperationJobDependencies",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OperationJobEvents",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OperationJobLines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OperationJobTasks",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OperationJobs",
                schema: "orders");
        }
    }
}
