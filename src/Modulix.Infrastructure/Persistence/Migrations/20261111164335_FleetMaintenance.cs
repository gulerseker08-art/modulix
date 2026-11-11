using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FleetMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastMaintenanceOn",
                schema: "fleet",
                table: "Vehicles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "NextMaintenanceDate",
                schema: "fleet",
                table: "Vehicles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NextMaintenanceKm",
                schema: "fleet",
                table: "Vehicles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OdometerKm",
                schema: "fleet",
                table: "Vehicles",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MaintenanceRecords",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KindKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OdometerKm = table.Column<int>(type: "int", nullable: true),
                    SupplierPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    NextDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextDueKm = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRecords", x => x.Id);
                    table.UniqueConstraint("AK_MaintenanceRecords_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_MaintenanceRecords_Vehicles_TenantId_VehicleId",
                        columns: x => new { x.TenantId, x.VehicleId },
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_NextMaintenance",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "TenantId", "NextMaintenanceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRecords_Vehicle",
                schema: "fleet",
                table: "MaintenanceRecords",
                columns: new[] { "TenantId", "VehicleId", "PerformedOn" });

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Araç kolonları null'lanabilir: geriye uyumlu.
            TenantIsolationSql.AddTable(migrationBuilder, "fleet", "MaintenanceRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "fleet", "MaintenanceRecords");

            migrationBuilder.DropTable(
                name: "MaintenanceRecords",
                schema: "fleet");

            migrationBuilder.DropIndex(
                name: "IX_Vehicles_NextMaintenance",
                schema: "fleet",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "LastMaintenanceOn",
                schema: "fleet",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "NextMaintenanceDate",
                schema: "fleet",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "NextMaintenanceKm",
                schema: "fleet",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "OdometerKm",
                schema: "fleet",
                table: "Vehicles");
        }
    }
}
