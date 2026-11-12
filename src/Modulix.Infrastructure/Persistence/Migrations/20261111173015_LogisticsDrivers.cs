using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsDrivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "logistics");

            migrationBuilder.CreateTable(
                name: "Drivers",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DriverType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CarrierPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultVehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_Drivers", x => x.Id);
                    table.UniqueConstraint("AK_Drivers_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Drivers_Carrier",
                schema: "logistics",
                table: "Drivers",
                columns: new[] { "TenantId", "CarrierPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Drivers_Vehicle",
                schema: "logistics",
                table: "Drivers",
                columns: new[] { "TenantId", "DefaultVehicleId" });

            migrationBuilder.CreateIndex(
                name: "UX_Drivers_Person",
                schema: "logistics",
                table: "Drivers",
                columns: new[] { "TenantId", "PersonId" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "Drivers");

            // Uygulama kullanıcısı şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[logistics] TO [modulix_app_role];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "Drivers");

            migrationBuilder.DropTable(
                name: "Drivers",
                schema: "logistics");
        }
    }
}
