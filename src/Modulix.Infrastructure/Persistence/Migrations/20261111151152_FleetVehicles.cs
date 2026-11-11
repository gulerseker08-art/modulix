using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FleetVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fleet");

            migrationBuilder.CreateTable(
                name: "Vehicles",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plate = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    KindKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Ownership = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CarrierPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ModelYear = table.Column<int>(type: "int", nullable: true),
                    Vin = table.Column<string>(type: "varchar(17)", unicode: false, maxLength: 17, nullable: true),
                    PayloadKg = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    VolumeM3 = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    LoadingMeters = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    PalletCapacity = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.UniqueConstraint("AK_Vehicles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Carrier",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "TenantId", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "UX_Vehicles_Plate",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "TenantId", "Plate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Vehicles_Vin",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "TenantId", "Vin" },
                unique: true,
                filter: "[Vin] IS NOT NULL");

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "fleet", "Vehicles");

            // Uygulama kullanıcısı yeni şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[fleet] TO [modulix_app_role];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "fleet", "Vehicles");

            migrationBuilder.DropTable(
                name: "Vehicles",
                schema: "fleet");
        }
    }
}
