using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TripId",
                schema: "logistics",
                table: "Shipments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Trips",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PlannedStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PlannedEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TrailerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DriverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CoDriverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CarrierPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.UniqueConstraint("AK_Trips_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Trips_Drivers_TenantId_CoDriverId",
                        columns: x => new { x.TenantId, x.CoDriverId },
                        principalSchema: "logistics",
                        principalTable: "Drivers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trips_Drivers_TenantId_DriverId",
                        columns: x => new { x.TenantId, x.DriverId },
                        principalSchema: "logistics",
                        principalTable: "Drivers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TripStops",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TripId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    StopType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ArrivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripStops", x => x.Id);
                    table.UniqueConstraint("AK_TripStops_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TripStops_Shipments_TenantId_ShipmentId",
                        columns: x => new { x.TenantId, x.ShipmentId },
                        principalSchema: "logistics",
                        principalTable: "Shipments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TripStops_Trips_TenantId_TripId",
                        columns: x => new { x.TenantId, x.TripId },
                        principalSchema: "logistics",
                        principalTable: "Trips",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_Trip",
                schema: "logistics",
                table: "Shipments",
                columns: new[] { "TenantId", "TripId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trips_Driver",
                schema: "logistics",
                table: "Trips",
                columns: new[] { "TenantId", "DriverId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trips_Status",
                schema: "logistics",
                table: "Trips",
                columns: new[] { "TenantId", "Status", "PlannedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_Trips_TenantId_CoDriverId",
                schema: "logistics",
                table: "Trips",
                columns: new[] { "TenantId", "CoDriverId" });

            migrationBuilder.CreateIndex(
                name: "IX_Trips_Vehicle",
                schema: "logistics",
                table: "Trips",
                columns: new[] { "TenantId", "VehicleId" });

            migrationBuilder.CreateIndex(
                name: "UX_Trips_Number",
                schema: "logistics",
                table: "Trips",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TripStops_Shipment",
                schema: "logistics",
                table: "TripStops",
                columns: new[] { "TenantId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_TripStops_Sequence",
                schema: "logistics",
                table: "TripStops",
                columns: new[] { "TenantId", "TripId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_Trips_TenantId_TripId",
                schema: "logistics",
                table: "Shipments",
                columns: new[] { "TenantId", "TripId" },
                principalSchema: "logistics",
                principalTable: "Trips",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Yeni tenant tabloları Row-Level Security politikasına eklenir (ADR-0002).
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "Trips");
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "TripStops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "TripStops");
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "Trips");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_Trips_TenantId_TripId",
                schema: "logistics",
                table: "Shipments");

            migrationBuilder.DropTable(
                name: "TripStops",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "Trips",
                schema: "logistics");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_Trip",
                schema: "logistics",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "TripId",
                schema: "logistics",
                table: "Shipments");
        }
    }
}
