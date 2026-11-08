using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportScheduleFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                schema: "core",
                table: "ReportSchedules",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Csv");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                schema: "core",
                table: "ReportSchedules");
        }
    }
}
