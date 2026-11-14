using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubProfileFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubProfileFeatures",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubProfileFeatures", x => x.Id);
                    table.UniqueConstraint("AK_SubProfileFeatures_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SubProfileFeatures_SubProfiles_TenantId_SubProfileId",
                        columns: x => new { x.TenantId, x.SubProfileId },
                        principalSchema: "meta",
                        principalTable: "SubProfiles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubProfileFeatures_TenantId_SubProfileId_Group_Value",
                schema: "meta",
                table: "SubProfileFeatures",
                columns: new[] { "TenantId", "SubProfileId", "Group", "Value" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına eklenir (ADR-0002).
            TenantIsolationSql.AddTable(migrationBuilder, "meta", "SubProfileFeatures");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "SubProfileFeatures");

            migrationBuilder.DropTable(
                name: "SubProfileFeatures",
                schema: "meta");
        }
    }
}
