using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalInboxEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedAt",
                schema: "core",
                table: "ApprovalRequestSteps",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EscalationRoleId",
                schema: "core",
                table: "ApprovalRequestSteps",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EscalationUserId",
                schema: "core",
                table: "ApprovalRequestSteps",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OnBehalfOf",
                schema: "core",
                table: "ApprovalRequestSteps",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalDelegations",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrincipalUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Link = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DedupeKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_TenantId_DelegateUserId_RevokedAt",
                schema: "core",
                table: "ApprovalDelegations",
                columns: new[] { "TenantId", "DelegateUserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_TenantId_PrincipalUserId_RevokedAt",
                schema: "core",
                table: "ApprovalDelegations",
                columns: new[] { "TenantId", "PrincipalUserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_UserId_DedupeKey",
                schema: "core",
                table: "Notifications",
                columns: new[] { "TenantId", "UserId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_UserId_ReadAt_CreatedAt",
                schema: "core",
                table: "Notifications",
                columns: new[] { "TenantId", "UserId", "ReadAt", "CreatedAt" });

            TenantIsolationSql.AddTable(migrationBuilder, "core", "ApprovalDelegations");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "Notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "Notifications");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ApprovalDelegations");

            migrationBuilder.DropTable(
                name: "ApprovalDelegations",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                schema: "core",
                table: "ApprovalRequestSteps");

            migrationBuilder.DropColumn(
                name: "EscalationRoleId",
                schema: "core",
                table: "ApprovalRequestSteps");

            migrationBuilder.DropColumn(
                name: "EscalationUserId",
                schema: "core",
                table: "ApprovalRequestSteps");

            migrationBuilder.DropColumn(
                name: "OnBehalfOf",
                schema: "core",
                table: "ApprovalRequestSteps");
        }
    }
}
