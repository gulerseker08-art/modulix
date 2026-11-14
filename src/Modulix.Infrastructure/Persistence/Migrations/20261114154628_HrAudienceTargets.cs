using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrAudienceTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudienceTargets",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudienceTargets", x => x.Id);
                    table.UniqueConstraint("AK_AudienceTargets_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_AudienceTargets_OneKind", "([BusinessUnitId] IS NULL AND [UserId] IS NOT NULL) OR ([BusinessUnitId] IS NOT NULL AND [UserId] IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudienceTargets_Content",
                schema: "hr",
                table: "AudienceTargets",
                columns: new[] { "TenantId", "ContentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AudienceTargets_User",
                schema: "hr",
                table: "AudienceTargets",
                columns: new[] { "TenantId", "UserId" });

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "AudienceTargets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "AudienceTargets");

            migrationBuilder.DropTable(
                name: "AudienceTargets",
                schema: "hr");
        }
    }
}
