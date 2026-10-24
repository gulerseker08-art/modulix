using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembershipInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvitedAt",
                schema: "platform",
                table: "Memberships",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSignInAt",
                schema: "platform",
                table: "Memberships",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId_InvitedAt",
                schema: "platform",
                table: "Memberships",
                columns: new[] { "TenantId", "InvitedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Memberships_TenantId_InvitedAt",
                schema: "platform",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "InvitedAt",
                schema: "platform",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "LastSignInAt",
                schema: "platform",
                table: "Memberships");
        }
    }
}
