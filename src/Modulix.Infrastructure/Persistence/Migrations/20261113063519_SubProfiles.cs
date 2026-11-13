using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubProfiles",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActivityCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubProfiles", x => x.Id);
                    table.UniqueConstraint("AK_SubProfiles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SubProfileOptions",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    TargetKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FieldType = table.Column<int>(type: "int", nullable: true),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    ShowInList = table.Column<bool>(type: "bit", nullable: false),
                    ChoicesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubProfileOptions", x => x.Id);
                    table.UniqueConstraint("AK_SubProfileOptions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SubProfileOptions_SubProfiles_TenantId_SubProfileId",
                        columns: x => new { x.TenantId, x.SubProfileId },
                        principalSchema: "meta",
                        principalTable: "SubProfiles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubProfileOptions_TenantId_SubProfileId_Kind_TargetKey_Label",
                schema: "meta",
                table: "SubProfileOptions",
                columns: new[] { "TenantId", "SubProfileId", "Kind", "TargetKey", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubProfiles_TenantId_Code",
                schema: "meta",
                table: "SubProfiles",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına eklenir (ADR-0002).
            TenantIsolationSql.AddTable(migrationBuilder, "meta", "SubProfiles");
            TenantIsolationSql.AddTable(migrationBuilder, "meta", "SubProfileOptions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "SubProfileOptions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "SubProfiles");

            migrationBuilder.DropTable(
                name: "SubProfileOptions",
                schema: "meta");

            migrationBuilder.DropTable(
                name: "SubProfiles",
                schema: "meta");
        }
    }
}
