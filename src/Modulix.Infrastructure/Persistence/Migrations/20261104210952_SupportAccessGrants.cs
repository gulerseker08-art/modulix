using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportAccessGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupportAccessGrants",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportAccessGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportAccessGrants_MembershipId_ExpiresAt",
                schema: "platform",
                table: "SupportAccessGrants",
                columns: new[] { "MembershipId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportAccessGrants_TenantId_GrantedAt",
                schema: "platform",
                table: "SupportAccessGrants",
                columns: new[] { "TenantId", "GrantedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportAccessGrants",
                schema: "platform");
        }
    }
}
