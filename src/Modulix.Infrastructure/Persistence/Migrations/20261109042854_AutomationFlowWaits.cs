using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationFlowWaits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                schema: "core",
                table: "AutomationRunSteps",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingUntil",
                schema: "core",
                table: "AutomationRunSteps",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartedAt",
                schema: "core",
                table: "AutomationRunSteps");

            migrationBuilder.DropColumn(
                name: "WaitingUntil",
                schema: "core",
                table: "AutomationRunSteps");
        }
    }
}
