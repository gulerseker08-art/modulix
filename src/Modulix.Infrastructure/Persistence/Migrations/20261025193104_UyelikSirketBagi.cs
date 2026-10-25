using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UyelikSirketBagi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MembershipCompanies",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipCompanies_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "core",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_MembershipId_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                columns: new[] { "MembershipId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_TenantId_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                columns: new[] { "TenantId", "CompanyId" });

            // Elle eklendi: üyelik–şirket bağı tenant verisidir (ADR-0002, Row-Level Security). Mevcut her etkin üyeliğe
            // tenant'ının varsayılan şirketi verilir (ADR-0008 adım 2, geçiş md.4): tek şirketli tenant'ta kimse erişim kaybetmez.
            TenantIsolationSql.AddTable(migrationBuilder, "core", "MembershipCompanies");
            migrationBuilder.Sql(MembershipCompanyBackfillSql.AllTenants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "MembershipCompanies");

            migrationBuilder.DropTable(
                name: "MembershipCompanies",
                schema: "core");
        }
    }
}
