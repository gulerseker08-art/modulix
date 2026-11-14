using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubProfileRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubProfileRules",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RuleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubProfileRules", x => x.Id);
                    table.UniqueConstraint("AK_SubProfileRules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SubProfileRules_SubProfiles_TenantId_SubProfileId",
                        columns: x => new { x.TenantId, x.SubProfileId },
                        principalSchema: "meta",
                        principalTable: "SubProfiles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubProfileRules_TenantId_Key",
                schema: "meta",
                table: "SubProfileRules",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubProfileRules_TenantId_SubProfileId",
                schema: "meta",
                table: "SubProfileRules",
                columns: new[] { "TenantId", "SubProfileId" });

            // Yeni tenant tabloları Row-Level Security politikasına eklenir (ADR-0002).
            TenantIsolationSql.AddTable(migrationBuilder, "meta", "SubProfileRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "SubProfileRules");

            migrationBuilder.DropTable(
                name: "SubProfileRules",
                schema: "meta");
        }
    }
}
