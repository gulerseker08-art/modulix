using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderHandover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Handovers",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OutOfScope = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Deliverables = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Commitments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    TargetStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TargetEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiredDocuments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CustomerPrerequisites = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PaymentConditions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Risks = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SpecialRequests = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Dependencies = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ResponsibleUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResponsibleTeam = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CompletionCriteria = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AcceptanceCriteria = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    InfoRequest = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationsActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SalesActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EscalatedRound = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Handovers", x => x.Id);
                    table.UniqueConstraint("AK_Handovers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Handovers_Dates", "[TargetEndDate] IS NULL OR [TargetStartDate] IS NULL OR [TargetEndDate] >= [TargetStartDate]");
                    table.CheckConstraint("CK_Handovers_Status", "[Status] IN ('Preparing','Ready','Sent','InfoRequested','Accepted')");
                    table.ForeignKey(
                        name: "FK_Handovers_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HandoverEvents",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandoverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HandoverEvents", x => x.Id);
                    table.UniqueConstraint("AK_HandoverEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_HandoverEvents_Handovers_TenantId_HandoverId",
                        columns: x => new { x.TenantId, x.HandoverId },
                        principalSchema: "orders",
                        principalTable: "Handovers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HandoverItems",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandoverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
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
                    table.PrimaryKey("PK_HandoverItems", x => x.Id);
                    table.UniqueConstraint("AK_HandoverItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_HandoverItems_Handovers_TenantId_HandoverId",
                        columns: x => new { x.TenantId, x.HandoverId },
                        principalSchema: "orders",
                        principalTable: "Handovers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HandoverEvents_TenantId_HandoverId_CreatedAt",
                schema: "orders",
                table: "HandoverEvents",
                columns: new[] { "TenantId", "HandoverId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HandoverEvents_TenantId_OrderId",
                schema: "orders",
                table: "HandoverEvents",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_HandoverItems_TenantId_HandoverId_Position",
                schema: "orders",
                table: "HandoverItems",
                columns: new[] { "TenantId", "HandoverId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Handovers_TenantId_Status_SentAt",
                schema: "orders",
                table: "Handovers",
                columns: new[] { "TenantId", "Status", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Handovers_Order",
                schema: "orders",
                table: "Handovers",
                columns: new[] { "TenantId", "OrderId" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (şema yetkisi OrderManagement migration'ında verildi).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "orders", table);
            }
        }

        private static readonly string[] TenantTables = ["Handovers", "HandoverItems", "HandoverEvents"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "orders", table);
            }

            migrationBuilder.DropTable(
                name: "HandoverEvents",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "HandoverItems",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Handovers",
                schema: "orders");
        }
    }
}
