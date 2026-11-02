using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ActualCost",
                schema: "marketing",
                table: "Campaigns",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Budget",
                schema: "marketing",
                table: "Campaigns",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCurrency",
                schema: "marketing",
                table: "Campaigns",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "TRY");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualCost",
                schema: "marketing",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "Budget",
                schema: "marketing",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CostCurrency",
                schema: "marketing",
                table: "Campaigns");
        }
    }
}
