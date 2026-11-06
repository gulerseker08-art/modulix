using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformRegimeProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegimeProfiles",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegimeProfileVersions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    VerificationNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegimeProfileVersions_RegimeProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "platform",
                        principalTable: "RegimeProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegimeProfiles_Code",
                schema: "platform",
                table: "RegimeProfiles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegimeProfileVersions_ProfileId_Number",
                schema: "platform",
                table: "RegimeProfileVersions",
                columns: new[] { "ProfileId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RegimeProfileVersions_Draft",
                schema: "platform",
                table: "RegimeProfileVersions",
                column: "ProfileId",
                unique: true,
                filter: "[State] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_RegimeProfileVersions_Published",
                schema: "platform",
                table: "RegimeProfileVersions",
                column: "ProfileId",
                unique: true,
                filter: "[State] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegimeProfileVersions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "RegimeProfiles",
                schema: "platform");
        }
    }
}
