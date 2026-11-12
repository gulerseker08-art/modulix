using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsFreightAndSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SettlementAmount",
                schema: "logistics",
                table: "Trips",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementCurrency",
                schema: "logistics",
                table: "Trips",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FreightAmount",
                schema: "logistics",
                table: "Shipments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreightCurrency",
                schema: "logistics",
                table: "Shipments",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettlementAmount",
                schema: "logistics",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "SettlementCurrency",
                schema: "logistics",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "FreightAmount",
                schema: "logistics",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "FreightCurrency",
                schema: "logistics",
                table: "Shipments");
        }
    }
}
