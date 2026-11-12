using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsRequestsShipments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransportRequests",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ExternalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiverPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransportModeKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PickupCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PickupAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DeliveryCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DeliveryBy = table.Column<DateOnly>(type: "date", nullable: true),
                    CargoDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    VolumeM3 = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    PalletCount = table.Column<int>(type: "int", nullable: true),
                    LoadingMeters = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    Hazardous = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportRequests", x => x.Id);
                    table.UniqueConstraint("AK_TransportRequests_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomerPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiverPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransportModeKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PickupCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PickupAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DeliveryCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DeliveryBy = table.Column<DateOnly>(type: "date", nullable: true),
                    CargoDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    VolumeM3 = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    PalletCount = table.Column<int>(type: "int", nullable: true),
                    LoadingMeters = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    Hazardous = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.UniqueConstraint("AK_Shipments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Shipments_TransportRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "logistics",
                        principalTable: "TransportRequests",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Customer",
                schema: "logistics",
                table: "Shipments",
                columns: new[] { "TenantId", "CustomerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Status",
                schema: "logistics",
                table: "Shipments",
                columns: new[] { "TenantId", "Status", "PickupDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Shipments_Request",
                schema: "logistics",
                table: "Shipments",
                columns: new[] { "TenantId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransportRequests_Customer",
                schema: "logistics",
                table: "TransportRequests",
                columns: new[] { "TenantId", "CustomerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportRequests_Status",
                schema: "logistics",
                table: "TransportRequests",
                columns: new[] { "TenantId", "Status", "PickupDate" });

            migrationBuilder.CreateIndex(
                name: "UX_TransportRequests_ExternalRef",
                schema: "logistics",
                table: "TransportRequests",
                columns: new[] { "TenantId", "Source", "ExternalRef" },
                unique: true,
                filter: "[ExternalRef] IS NOT NULL");

            // Talep ve sevkiyat tenant verisidir: Row-Level Security politikasına eklenir.
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "TransportRequests");
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "Shipments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "Shipments");
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "TransportRequests");

            migrationBuilder.DropTable(
                name: "Shipments",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "TransportRequests",
                schema: "logistics");
        }
    }
}
