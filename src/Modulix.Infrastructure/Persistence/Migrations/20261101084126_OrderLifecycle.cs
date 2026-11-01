using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderLines_Quantity",
                schema: "orders",
                table: "OrderLines");

            migrationBuilder.AddColumn<decimal>(
                name: "CancelledQuantity",
                schema: "orders",
                table: "OrderLines",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ChangeRequests",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Changes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Impact = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    NetDelta = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxDelta = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeRequests", x => x.Id);
                    table.UniqueConstraint("AK_ChangeRequests_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ChangeRequests_Status", "[Status] IN ('Pending','Approved','Rejected','Withdrawn')");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderLines_Quantity",
                schema: "orders",
                table: "OrderLines",
                sql: "[Quantity] > 0 AND [DeliveredQuantity] >= 0 AND [CancelledQuantity] >= 0 AND [DeliveredQuantity] + [CancelledQuantity] <= [Quantity]");

            migrationBuilder.CreateIndex(
                name: "UX_ChangeRequests_OnePending",
                schema: "orders",
                table: "ChangeRequests",
                columns: new[] { "TenantId", "OrderId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "UX_ChangeRequests_Sequence",
                schema: "orders",
                table: "ChangeRequests",
                columns: new[] { "TenantId", "OrderId", "Sequence" },
                unique: true);

            // Yeni tenant tablosu Row-Level Security politikasına girer (şema yetkisi OrderManagement migration'ında verildi).
            TenantIsolationSql.AddTable(migrationBuilder, "orders", "ChangeRequests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "orders", "ChangeRequests");

            migrationBuilder.DropTable(
                name: "ChangeRequests",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderLines_Quantity",
                schema: "orders",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "CancelledQuantity",
                schema: "orders",
                table: "OrderLines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderLines_Quantity",
                schema: "orders",
                table: "OrderLines",
                sql: "[Quantity] > 0 AND [DeliveredQuantity] >= 0 AND [DeliveredQuantity] <= [Quantity]");
        }
    }
}
