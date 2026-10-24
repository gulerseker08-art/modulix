using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamsAndRecordScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Scope",
                schema: "core",
                table: "Roles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Teams",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.UniqueConstraint("AK_Teams_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalSchema: "platform",
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TenantId_TeamId",
                        columns: x => new { x.TenantId, x.TeamId },
                        principalSchema: "core",
                        principalTable: "Teams",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TenantId_MembershipId",
                schema: "core",
                table: "TeamMembers",
                columns: new[] { "TenantId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TenantId_TeamId_MembershipId",
                schema: "core",
                table: "TeamMembers",
                columns: new[] { "TenantId", "TeamId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_TenantId_Name",
                schema: "core",
                table: "Teams",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Elle eklendi: ekipler tenant verisidir (ADR-0002, Row-Level Security).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "Teams");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "TeamMembers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "TeamMembers");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "Teams");

            migrationBuilder.DropTable(
                name: "TeamMembers",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Teams",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "core",
                table: "Roles");
        }
    }
}
