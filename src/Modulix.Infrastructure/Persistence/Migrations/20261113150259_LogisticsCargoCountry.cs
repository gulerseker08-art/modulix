using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsCargoCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryCountry",
                schema: "logistics",
                table: "TransportRequests",
                type: "char(2)",
                unicode: false,
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "TR");

            migrationBuilder.AddColumn<string>(
                name: "PickupCountry",
                schema: "logistics",
                table: "TransportRequests",
                type: "char(2)",
                unicode: false,
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "TR");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryCountry",
                schema: "logistics",
                table: "Shipments",
                type: "char(2)",
                unicode: false,
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "TR");

            migrationBuilder.AddColumn<string>(
                name: "PickupCountry",
                schema: "logistics",
                table: "Shipments",
                type: "char(2)",
                unicode: false,
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "TR");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryCountry",
                schema: "logistics",
                table: "TransportRequests");

            migrationBuilder.DropColumn(
                name: "PickupCountry",
                schema: "logistics",
                table: "TransportRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryCountry",
                schema: "logistics",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "PickupCountry",
                schema: "logistics",
                table: "Shipments");
        }
    }
}
