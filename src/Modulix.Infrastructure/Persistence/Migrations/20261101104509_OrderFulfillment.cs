using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ActualDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReceiverName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceiverNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RescheduledFromId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RescheduleReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.UniqueConstraint("AK_Deliveries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Deliveries_Status", "[Status] IN ('Planned','Delivered','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_Deliveries_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceStarts",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlannedStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ActualStartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CustomerConfirmedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfirmationNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DelayReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceStarts", x => x.Id);
                    table.UniqueConstraint("AK_ServiceStarts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ServiceStarts_Status", "[Status] IN ('Planned','Started','Completed')");
                    table.ForeignKey(
                        name: "FK_ServiceStarts_OrderLines_TenantId_OrderLineId",
                        columns: x => new { x.TenantId, x.OrderLineId },
                        principalSchema: "orders",
                        principalTable: "OrderLines",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceStarts_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryEvidence",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryEvidence", x => x.Id);
                    table.UniqueConstraint("AK_DeliveryEvidence_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_DeliveryEvidence_Size", "[SizeBytes] > 0 AND [SizeBytes] <= 2097152");
                    table.ForeignKey(
                        name: "FK_DeliveryEvidence_Deliveries_TenantId_DeliveryId",
                        columns: x => new { x.TenantId, x.DeliveryId },
                        principalSchema: "orders",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryLines",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DeliveredQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryLines", x => x.Id);
                    table.UniqueConstraint("AK_DeliveryLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_DeliveryLines_Quantity", "[PlannedQuantity] > 0 AND [DeliveredQuantity] >= 0");
                    table.ForeignKey(
                        name: "FK_DeliveryLines_Deliveries_TenantId_DeliveryId",
                        columns: x => new { x.TenantId, x.DeliveryId },
                        principalSchema: "orders",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeliveryLines_OrderLines_TenantId_OrderLineId",
                        columns: x => new { x.TenantId, x.OrderLineId },
                        principalSchema: "orders",
                        principalTable: "OrderLines",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_OrderId",
                schema: "orders",
                table: "Deliveries",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_Status_PlannedDate",
                schema: "orders",
                table: "Deliveries",
                columns: new[] { "TenantId", "Status", "PlannedDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Deliveries_Number",
                schema: "orders",
                table: "Deliveries",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryEvidence_TenantId_DeliveryId",
                schema: "orders",
                table: "DeliveryEvidence",
                columns: new[] { "TenantId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLines_TenantId_OrderId_OrderLineId",
                schema: "orders",
                table: "DeliveryLines",
                columns: new[] { "TenantId", "OrderId", "OrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLines_TenantId_OrderLineId",
                schema: "orders",
                table: "DeliveryLines",
                columns: new[] { "TenantId", "OrderLineId" });

            migrationBuilder.CreateIndex(
                name: "UX_DeliveryLines_Line",
                schema: "orders",
                table: "DeliveryLines",
                columns: new[] { "TenantId", "DeliveryId", "OrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStarts_TenantId_OrderId",
                schema: "orders",
                table: "ServiceStarts",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStarts_TenantId_Status_PlannedStartDate",
                schema: "orders",
                table: "ServiceStarts",
                columns: new[] { "TenantId", "Status", "PlannedStartDate" });

            migrationBuilder.CreateIndex(
                name: "UX_ServiceStarts_Line",
                schema: "orders",
                table: "ServiceStarts",
                columns: new[] { "TenantId", "OrderLineId" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (şema yetkisi OrderManagement migration'ında verildi).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "orders", table);
            }
        }

        private static readonly string[] TenantTables = ["Deliveries", "DeliveryLines", "DeliveryEvidence", "ServiceStarts"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "orders", table);
            }

            migrationBuilder.DropTable(
                name: "DeliveryEvidence",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "DeliveryLines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "ServiceStarts",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Deliveries",
                schema: "orders");
        }
    }
}
